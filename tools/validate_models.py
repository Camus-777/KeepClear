"""Real-weight, offline stream checks and a repeatable Chinese TTS fixture report."""
import importlib.util
import json
from pathlib import Path
import time
import urllib.request
import wave

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("model_worker", ROOT / "workers" / "model_worker.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


def stages(mode):
    result = []
    if "alkove" in mode:
        result.append(worker.AlkoveStage(ROOT / "models" / "alkove"))
    if "denoiser" in mode:
        result.append(worker.DenoiserStage(ROOT / "models" / "denoiser" / "dns48-state.pt"))
    return result


def process(pcm, mode, sizes=(256,)):
    chain = worker.SerialChain(stages(mode))
    result, timing, emitted = [], [], []
    at = 0
    while at < len(pcm):
        count = sizes[len(timing) % len(sizes)]
        block = pcm[at:at + count].tolist()
        start = time.perf_counter()
        part = chain.process(block)
        timing.append((time.perf_counter() - start) * 1000)
        emitted.append(len(part))
        result.extend(part)
        at += len(block)
    output = np.asarray(result, dtype=np.float32)
    assert np.isfinite(output).all()
    assert len(pcm) - 2048 <= len(output) <= len(pcm)
    return output, {
        "input_samples": len(pcm), "output_samples": len(output), "calls": len(timing),
        "first_nonempty_call_1based": next((i + 1 for i, n in enumerate(emitted) if n), None),
        "mean_ms": float(np.mean(timing)), "p95_ms": float(np.percentile(timing, 95)),
        "max_ms": float(max(timing)), "compute_rtf": float(sum(timing) / (len(pcm) / 16)),
        "output_peak": float(np.max(np.abs(output))),
    }


def write_wave(path, pcm):
    with wave.open(str(path), "wb") as stream:
        stream.setnchannels(1)
        stream.setsampwidth(2)
        stream.setframerate(16000)
        stream.writeframes((np.clip(pcm, -1, 1) * 32767).astype("<i2").tobytes())


def main():
    # Any unexpected model download during loading/processing is a test failure.
    def deny_network(*args, **kwargs):
        raise AssertionError("Inference attempted a network request")
    urllib.request.urlopen = deny_network
    with wave.open(str(ROOT / "artifacts" / "fixtures" / "tts-clean.wav"), "rb") as stream:
        assert (stream.getnchannels(), stream.getsampwidth(), stream.getframerate()) == (1, 2, 16000)
        speech = np.frombuffer(stream.readframes(stream.getnframes()), dtype="<i2").astype(np.float32) / 32768
    rng = np.random.default_rng(20261114)
    reference = np.concatenate([np.zeros(16000), speech, np.zeros(16000)]).astype(np.float32)
    noise = rng.normal(0, 0.012, len(reference)).astype(np.float32)
    noise += (0.006 * np.sin(2 * np.pi * 120 * np.arange(len(noise)) / 16000)).astype(np.float32)
    noisy = np.clip(reference + noise, -.95, .95)
    write_wave(ROOT / "artifacts" / "fixtures" / "tts-noisy.wav", noisy)
    report = {"fixture": "Locally generated Microsoft Huihui Chinese TTS + seeded synthetic noise; no user recording",
              "sample_rate": 16000, "modes": {}, "network_disabled": True}
    for mode in [("alkove",), ("denoiser",), ("alkove", "denoiser")]:
        output, stats = process(noisy, mode)
        key = "-".join(mode)
        write_wave(ROOT / "artifacts" / "fixtures" / ("tts-" + key + ".wav"), output)
        # Leading noise-only interval, excluding initial transient.
        stats["noise_input_rms"] = float(np.sqrt(np.mean(noisy[4000:12000] ** 2)))
        stats["noise_output_rms"] = float(np.sqrt(np.mean(output[4000:12000] ** 2)))
        assert np.max(np.abs(output[16000:16000 + len(speech)])) > 0.001, "Speech was completely muted"
        report["modes"][key] = stats
    # Different caller packet sizes must preserve persistent states and waveform.
    fixed, _ = process(noisy[:16000], ("alkove", "denoiser"))
    irregular, _ = process(noisy[:16000], ("alkove", "denoiser"), (137, 511, 83, 256))
    assert len(fixed) == len(irregular)
    difference = float(np.max(np.abs(fixed - irregular)))
    assert difference < 1e-5
    report["packet_boundary_max_abs_difference"] = difference
    # An explicit two-stage composition must equal the configured serial host.
    a, d = stages(("alkove", "denoiser"))
    explicit = []
    for at in range(0, 16000, 256):
        intermediate = a.process(noisy[at:min(at + 256, 16000)].tolist())
        if intermediate:
            explicit.extend(d.process(intermediate))
    assert np.allclose(fixed, explicit, atol=1e-6, rtol=0)
    report["serial_order_matches_explicit_alkove_then_denoiser"] = True
    invalid = ROOT / "artifacts" / "fixtures" / "invalid-state.pt"
    invalid.write_bytes(b"not the pinned DNS48 weights")
    try:
        worker.DenoiserStage(invalid)
    except ValueError as exc:
        assert "校验失败" in str(exc)
    else:
        raise AssertionError("Corrupted DNS48 checkpoint was accepted")
    report["corrupted_denoiser_weights_rejected"] = True
    empty = ROOT / "artifacts" / "fixtures" / "missing-alkove"
    empty.mkdir(exist_ok=True)
    try:
        worker.AlkoveStage(empty)
    except FileNotFoundError:
        pass
    else:
        raise AssertionError("Missing Alkove weights were accepted")
    report["missing_alkove_weights_rejected"] = True
    assert report["modes"]["alkove-denoiser"]["compute_rtf"] < 1.0, "Slower than realtime on this machine"
    path = ROOT / "artifacts" / "model-validation.json"
    path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
