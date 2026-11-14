import importlib.util
import io
import math
from pathlib import Path
import struct
import unittest

spec = importlib.util.spec_from_file_location("worker", Path(__file__).resolve().parents[1] / "workers" / "model_worker.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class WorkerTests(unittest.TestCase):
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


if __name__ == "__main__":
    unittest.main()
