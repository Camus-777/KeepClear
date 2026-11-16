"""Optional offline model host. Only Python stdlib is required for bypass/tests.

Protocol: CV01 + int32 rate, then repeated int32 sample count + float32 LE PCM.
All third-party stdout is redirected to stderr so it cannot corrupt the pipe.
"""
import argparse
import contextlib
import hashlib
import math
import os
from pathlib import Path
import struct
import sys
import json
import time

RATE = 16000
MAX_SAMPLES = 32000
DNS_SHA = "11decc9d8e3f0998a64caed6b57b68a40cb200a627996a636625f20bdb91f1d6"


class SerialChain:
    def __init__(self, stages, metrics=False):
        self.stages = stages
        self.metrics = metrics
        self.report_at = time.perf_counter()
        self.stage_times = {}

    def process(self, pcm):
        for stage in self.stages:
            if not pcm:
                break
            started = time.perf_counter() if self.metrics else 0
            pcm = stage.process(pcm)
            if self.metrics:
                elapsed = (time.perf_counter() - started) * 1000
                values = self.stage_times.setdefault(type(stage).__name__, [0, 0.0, 0.0])
                values[0] += 1
                values[1] += elapsed
                values[2] = max(values[2], elapsed)
            if len(pcm) > MAX_SAMPLES or any(not math.isfinite(x) for x in pcm):
                raise ValueError("model returned invalid PCM")
        if self.metrics and time.perf_counter() - self.report_at >= 1:
            report = {name: {"calls": v[0], "mean_ms": round(v[1] / v[0], 3), "max_ms": round(v[2], 3)} for name, v in self.stage_times.items()}
            print("KC_METRIC " + json.dumps(report), file=sys.stderr, flush=True)
            self.stage_times.clear()
            self.report_at = time.perf_counter()
        return pcm


class AlkoveStage:
    def __init__(self, folder):
        import numpy as np
        import alkove
        if alkove.__version__ != "0.4.0":
            raise RuntimeError("KeepClear 此版本需要 alkove 0.4.0，请运行 setup-models.ps1")
        if not os.path.isdir(folder):
            raise FileNotFoundError("请将本地 Alkove .alk 文件放入 models/alkove")
        self.np = np
        # A local folder prevents automatic network/model downloads.
        # Alkove 0.4.0's Processor validates Focus + Cue even with cue=False.
        # Cue is supplied to satisfy that SDK contract; no VAD gate is enabled.
        models = alkove.Models.load(["focus", "cue"], dir=folder)
        self.processor = alkove.Processor(models, focus=True, cue=False, normalize=False, rate=RATE)

    def process(self, pcm):
        return self.processor.push(self.np.asarray(pcm, dtype=self.np.float32)).audio.tolist()


class DenoiserStage:
    def __init__(self, checkpoint):
        import torch
        # Only the original upstream inference modules are bundled, not its
        # training/evaluation stack or a second audio-device implementation.
        vendor = Path(__file__).resolve().parents[1] / "third_party" / "denoiser"
        sys.path.insert(0, str(vendor))
        from denoiser.demucs import Demucs, DemucsStreamer
        if not os.path.isfile(checkpoint):
            raise FileNotFoundError("请将 Denoiser dns48 纯 state_dict 放入 models/denoiser/dns48-state.pt")
        digest = hashlib.sha256()
        with open(checkpoint, "rb") as stream:
            for part in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(part)
        if digest.hexdigest() != DNS_SHA:
            raise ValueError("Denoiser DNS48 权重校验失败，请重新运行模型准备步骤")
        self.torch = torch
        # Small streaming CPU frames run faster through ATen than oneDNN's
        # recurrent dispatch on the tested Ryzen host. Keep float32 inference.
        torch.backends.mkldnn.enabled = False
        self.flush_denormal = torch.set_flush_denormal(True)
        torch.set_num_threads(2 if (os.cpu_count() or 1) >= 4 else 1)
        if torch.get_num_interop_threads() != 1:
            torch.set_num_interop_threads(1)
        # Only tensor state dictionaries: no arbitrary pickled model execution.
        state = torch.load(checkpoint, map_location="cpu", weights_only=True)
        model = Demucs(hidden=48, sample_rate=RATE, rescale=0)
        model.load_state_dict(state, strict=True)
        model.eval()
        # Warm CPU kernels before opening the live microphone. Re-create the
        # streamer afterwards so warm-up samples cannot leak into the session.
        with torch.inference_mode():
            warmup = DemucsStreamer(model, dry=0, num_frames=1)
            warmup.feed(torch.zeros(1, 4096))
        self.streamer = DemucsStreamer(model, dry=0, num_frames=1)
        print("KC_CPU " + json.dumps({"torch": torch.__version__, "backend": "aten", "threads": torch.get_num_threads(),
            "interop_threads": torch.get_num_interop_threads(), "flush_denormal": self.flush_denormal}), file=sys.stderr, flush=True)

    def process(self, pcm):
        with self.torch.inference_mode():
            frame = self.torch.tensor(pcm, dtype=self.torch.float32).reshape(1, -1)
            return self.streamer.feed(frame)[0].tolist()


def read_exact(stream, size):
    parts = bytearray()
    while len(parts) < size:
        part = stream.read(size - len(parts))
        if not part:
            raise EOFError("truncated audio packet")
        parts.extend(part)
    return parts


def serve(chain, source, destination):
    destination.write(b"CV01" + struct.pack("<i", RATE))
    destination.flush()
    while True:
        header = source.read(4)
        if not header:
            return
        if len(header) < 4:
            header += read_exact(source, 4 - len(header))
        count, = struct.unpack("<i", header)
        if count < 0 or count > MAX_SAMPLES:
            raise ValueError("audio packet too large or negative")
        pcm = list(struct.unpack("<%df" % count, read_exact(source, count * 4)))
        if any(not math.isfinite(x) for x in pcm):
            raise ValueError("non-finite input PCM")
        with contextlib.redirect_stdout(sys.stderr):
            result = chain.process(pcm)
        if len(result) > MAX_SAMPLES or any(not math.isfinite(x) for x in result):
            raise ValueError("invalid output PCM")
        destination.write(struct.pack("<i", len(result)))
        destination.write(struct.pack("<%df" % len(result), *result))
        destination.flush()


def main():
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", required=True)
    parser.add_argument("--alkove", action="store_true")
    parser.add_argument("--denoiser", action="store_true")
    args = parser.parse_args()
    output = sys.stdout.buffer
    # Keep protocol stdout private even during model imports/loading.
    with contextlib.redirect_stdout(sys.stderr):
        stages = []
        if args.alkove:
            stages.append(AlkoveStage(os.path.join(args.models, "alkove")))
        if args.denoiser:
            stages.append(DenoiserStage(os.path.join(args.models, "denoiser", "dns48-state.pt")))
    serve(SerialChain(stages, metrics=True), sys.stdin.buffer, output)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print("%s: %s" % (type(exc).__name__, exc), file=sys.stderr, flush=True)
        sys.exit(1)
