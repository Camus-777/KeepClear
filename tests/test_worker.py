import importlib.util
import io
import math
from pathlib import Path
import struct
import unittest
import contextlib
from unittest.mock import patch
import json
try:
    import numpy as np
except ImportError:
    np = None

spec = importlib.util.spec_from_file_location("worker", Path(__file__).resolve().parents[1] / "workers" / "model_worker.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class WorkerTests(unittest.TestCase):
    def test_diagnostics_preserve_samples(self):
        class Identity:
            def process(self, pcm): return pcm
        chain = worker.SerialChain([Identity()], metrics=True)
        chain.report_at -= 2
        output = io.StringIO()
        with contextlib.redirect_stderr(output):
            result = chain.process([0.25, -0.5])
        report = json.loads(output.getvalue().split("KC_METRIC ", 1)[1])
        self.assertEqual(result, [0.25, -0.5])
        self.assertGreater(report['worker']['wall_interval_ms'], 0)
        self.assertGreaterEqual(report['worker']['cpu_interval_ms'], 0)

    def test_state_diagnostic_failure_is_nonfatal(self):
        class DiagnosticFailure:
            def process(self, pcm): return pcm
            def diagnostic_state(self): raise RuntimeError('fixture')
        with patch.object(worker, 'DenoiserStage', DiagnosticFailure):
            chain = worker.SerialChain([DiagnosticFailure()], metrics=True)
            chain.report_at -= 2
            with contextlib.redirect_stderr(io.StringIO()) as output:
                self.assertEqual(chain.process([0.25]), [0.25])
            report = json.loads(output.getvalue().split("KC_METRIC ", 1)[1])
            self.assertEqual(report['DiagnosticFailure']['state_unavailable'], 'RuntimeError')

    def test_serial_order(self):
        class Add:
            def process(self, pcm): return [x + 1 for x in pcm]
        class Multiply:
            def process(self, pcm): return [x * 3 for x in pcm]
        self.assertEqual(worker.SerialChain([Add(), Multiply()]).process([1]), [6])

    def test_variable_output(self):
        class Buffering:
            def __init__(self): self.pending = []
            def process(self, pcm):
                self.pending.extend(pcm)
                if len(self.pending) < 4: return []
                result, self.pending = self.pending, []
                return result
        chain = worker.SerialChain([Buffering()])
        self.assertEqual(chain.process([1, 2]), [])
        self.assertEqual(chain.process([3, 4]), [1, 2, 3, 4])

    def test_wire_roundtrip(self):
        packet = struct.pack("<i3f", 3, -0.25, 0.0, 0.25)
        output = io.BytesIO()
        worker.serve(worker.SerialChain([]), io.BytesIO(packet), output)
        self.assertEqual(output.getvalue(), b"CV01" + struct.pack("<i", 16000) + packet)

    def test_negative_and_oversized(self):
        for size in [-1, 32001]:
            with self.assertRaises(ValueError):
                worker.serve(worker.SerialChain([]), io.BytesIO(struct.pack("<i", size)), io.BytesIO())

    def test_truncation(self):
        with self.assertRaises(EOFError):
            worker.serve(worker.SerialChain([]), io.BytesIO(struct.pack("<i", 2) + b"\0"), io.BytesIO())

    def test_nonfinite_input(self):
        for value in [math.nan, math.inf]:
            with self.assertRaises(ValueError):
                worker.serve(worker.SerialChain([]), io.BytesIO(struct.pack("<if", 1, value)), io.BytesIO())

    def test_nonfinite_stage(self):
        class Bad:
            def process(self, pcm): return [math.nan]
        with self.assertRaises(ValueError): worker.SerialChain([Bad()]).process([1])

    def test_model_output_bound(self):
        class Huge:
            def process(self, pcm): return [0.0] * 32001
        with self.assertRaises(ValueError): worker.SerialChain([Huge()]).process([1])


@unittest.skipIf(np is None, "NumPy integration checks require model dependencies")
class ArrayWorkerTests(unittest.TestCase):
    def run_packet(self, chain, values):
        packet = struct.pack("<i", len(values)) + np.asarray(values, dtype="<f4").tobytes()
        output = io.BytesIO()
        worker.serve(chain, io.BytesIO(packet), output, array_module=np)
        return packet, output.getvalue()

    def test_array_wire_exact(self):
        for values in [[], [0, -0.0, 0.25, -0.5], [1e-30, -1e-30]]:
            packet, actual = self.run_packet(worker.SerialChain([], array_module=np), values)
            self.assertEqual(actual, b"CV01" + struct.pack("<i", 16000) + packet)

    def test_array_nonfinite(self):
        for value in [math.nan, math.inf, -math.inf]:
            with self.assertRaises(ValueError):
                self.run_packet(worker.SerialChain([], array_module=np), [value])

    def test_array_serial_order_and_empty(self):
        class Add:
            def process(self, pcm): return pcm + np.float32(1)
        class Multiply:
            def process(self, pcm): return pcm * np.float32(3)
        chain = worker.SerialChain([Add(), Multiply()], array_module=np)
        self.assertEqual(chain.process(np.array([1], dtype=np.float32)).tolist(), [6])
        self.assertEqual(len(chain.process(np.array([], dtype=np.float32))), 0)

    def test_array_stage_rejects_nonfinite_and_shape(self):
        class Bad:
            def __init__(self, value): self.value = value
            def process(self, pcm): return self.value
        for result in [np.array([math.nan]), np.zeros((2, 2)), np.zeros(32001)]:
            with self.assertRaises(ValueError):
                worker.SerialChain([Bad(result)], array_module=np).process(np.zeros(256))

    def test_array_variable_output(self):
        class Buffering:
            def __init__(self): self.pending = np.array([], dtype=np.float32)
            def process(self, pcm):
                self.pending = np.concatenate([self.pending, pcm])
                if len(self.pending) < 4: return np.array([], dtype=np.float32)
                result = self.pending.copy()
                self.pending = np.array([], dtype=np.float32)
                return result
        chain = worker.SerialChain([Buffering()], array_module=np)
        self.assertEqual(chain.process(np.array([1, 2], dtype=np.float32)).tolist(), [])
        self.assertEqual(chain.process(np.array([3, 4], dtype=np.float32)).tolist(), [1, 2, 3, 4])

    def test_array_writable_and_truncated(self):
        class Mutate:
            def process(self, pcm):
                pcm[0] = -0.25
                return pcm
        _, actual = self.run_packet(worker.SerialChain([Mutate()], array_module=np), [0.25])
        self.assertEqual(struct.unpack("<f", actual[-4:])[0], -0.25)
        with self.assertRaises(EOFError):
            worker.serve(worker.SerialChain([], array_module=np), io.BytesIO(struct.pack("<i", 2) + b"x"), io.BytesIO(), array_module=np)


if __name__ == "__main__":
    unittest.main()
