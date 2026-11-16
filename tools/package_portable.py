"""Build an app-local Windows CPython runtime from the locked model environment.

No installation/freezing framework. The official embed archive is pinned by SHA256;
installed distribution files are verified against their wheel RECORD before copying.
"""
import argparse
import base64
import hashlib
import importlib.metadata as metadata
import json
from pathlib import Path
import re
import shutil
import sys
import os
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
PYTHON_VERSION = "3.12.10"
EMBED_URL = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip"
EMBED_SHA = "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3"


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def locked_packages():
    result = {}
    for line in (ROOT / "requirements-models.lock.txt").read_text().splitlines():
        match = re.match(r"([\w.-]+)==([^ ]+) --hash=sha256:([a-f0-9]{64})", line)
        if match:
            result[match[1]] = {"version": match[2], "wheel_sha256": match[3]}
    if len(result) != 12:
        raise ValueError("Expected all 12 locked model distributions")
    return result


def copy_runtime_packages(destination):
    site = Path(sys.prefix) / "Lib" / "site-packages"
    if not site.is_dir():
        raise RuntimeError("Run with the project's Windows model venv")
    site = site.resolve()
    target_site = destination / "runtime" / "Lib" / "site-packages"
    packages = locked_packages()
    omitted_bytes = 0
    for name, entry in packages.items():
        distribution = metadata.distribution(name)
        if distribution.version != entry["version"]:
            raise ValueError(f"Wrong {name} version: {distribution.version}")
        copied = 0
        for item in distribution.files or []:
            source = Path(distribution.locate_file(item)).resolve()
            if not source.is_relative_to(site):
                continue  # Development command-line entry points outside site-packages.
            relative = source.relative_to(site)
            if "__pycache__" in relative.parts or source.suffix == ".pyc":
                continue
            # PyTorch's wheel includes gigabytes of static compilation libraries.
            if source.suffix.lower() in {".lib", ".a", ".pdb"} or relative.parts[:2] in {
                ("torch", "include"), ("torch", "share"), ("torch", "bin")
            }:
                omitted_bytes += source.stat().st_size
                continue
            if not source.is_file():
                raise FileNotFoundError(source)
            if item.hash:
                expected = item.hash.value
                actual = base64.urlsafe_b64encode(bytes.fromhex(sha256(source))).decode().rstrip("=")
                if item.hash.mode != "sha256" or actual != expected:
                    raise ValueError(f"Installed wheel file was modified: {relative}")
            target = target_site / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            copied += 1
        entry["copied_files"] = copied
        print(f"Copied {name} {distribution.version}: {copied} files", flush=True)
    # The official NumPy wheel already carries the Microsoft C++ runtime DLL.
    # Expose the same unmodified binary under the standard name required by Torch.
    cpp_runtime = list((target_site / "numpy.libs").glob("msvcp140-*.dll"))
    if len(cpp_runtime) != 1:
        raise FileNotFoundError("Expected the NumPy wheel's app-local MSVCP runtime")
    shutil.copy2(cpp_runtime[0], destination / "runtime" / "msvcp140.dll")
    return packages, omitted_bytes, cpp_runtime[0].relative_to(destination).as_posix()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--archive-only", action="store_true")
    parser.add_argument("--application-dir", type=Path, default=ROOT / "dist")
    args = parser.parse_args()
    output = args.output.resolve()
    release = (ROOT / "release").resolve()
    if not output.is_relative_to(release) or output == release:
        raise ValueError("Package output must be a child directory of this project's release folder")
    if args.archive_only:
        archive_package(output)
        return
    if sys.version_info[:3] != (3, 12, 10) or sys.maxsize <= 2 ** 32:
        raise RuntimeError("Use the verified Windows x64 CPython 3.12.10 model environment")
    if output.exists():
        raise FileExistsError(f"Preserving existing output: {output}")
    cache = ROOT / "artifacts" / "portable-cache"
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / EMBED_URL.rsplit("/", 1)[1]
    if not archive.exists():
        with urllib.request.urlopen(EMBED_URL, timeout=90) as response:
            archive.write_bytes(response.read())
    if sha256(archive) != EMBED_SHA:
        raise ValueError("Official Python embed archive checksum mismatch")
    output.mkdir(parents=True)
    runtime = output / "runtime"
    runtime.mkdir()
    with zipfile.ZipFile(archive) as stream:
        for name in stream.namelist():
            if not (runtime / name).resolve().is_relative_to(runtime.resolve()):
                raise ValueError("Unsafe runtime archive member")
        stream.extractall(runtime)
    # Explicit relative paths; do not import site, .pth files, user packages or registry paths.
    (runtime / "python312._pth").write_text("python312.zip\n.\nLib/site-packages\n", encoding="ascii")
    packages, omitted_bytes, cpp_source = copy_runtime_packages(output)
    for name in ["KeepClear.exe", "KeepClear.exe.config"]:
        shutil.copy2(args.application_dir.resolve() / name, output / name)
    shutil.copytree(ROOT / "workers", output / "workers", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    shutil.copytree(ROOT / "third_party", output / "third_party", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    model_manifest = json.loads((ROOT / "models" / "MANIFEST.json").read_text())
    (output / "models").mkdir()
    shutil.copy2(ROOT / "models" / "README.md", output / "models" / "README.md")
    shutil.copy2(ROOT / "models" / "MANIFEST.json", output / "models" / "MANIFEST.json")
    for key in ["alkove", "cue", "denoiser"]:
        entry = model_manifest[key]
        source = ROOT / "models" / entry["file"]
        if sha256(source) != entry["sha256"]:
            raise ValueError(f"Model checksum mismatch: {key}")
        target = output / "models" / entry["file"]
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    shutil.copytree(ROOT / "docs", output / "docs")
    shutil.copytree(ROOT / "assets", output / "assets")
    for name in ["README.md", "README.zh-CN.md", "QUICKSTART.txt", "THIRD-PARTY.md", "requirements-models.lock.txt", "LICENSE", "NOTICE", "LICENSING.md", "CHANGELOG.md"]:
        shutil.copy2(ROOT / name, output / name)
    shutil.copy2(ROOT / "docs" / "PORTABLE-QUICKSTART.txt", output / "开始使用.txt")
    manifest = {
        "format": 1, "application": "KeepClear", "version": "0.3.2", "architecture": "Windows x64",
        "python": {"version": PYTHON_VERSION, "source": EMBED_URL, "sha256": EMBED_SHA},
        "packages": packages, "omitted_build_bytes": omitted_bytes,
        "cpp_runtime_alias_source": cpp_source,
        "prerequisites": ["Windows 10/11 x64", ".NET Framework 4.8", "Windows administrator approval if VB-CABLE is missing"],
        "bundled_vbcable": {"resource": "KeepClear.VBCable.zip", "package": "VBCABLE_Driver_Pack46.zip", "sha256": "bc44e8974191a5a2427d6c979e2dab08dbb09925f3f435dfa2f0ba3e45b6eb90", "origin": "https://vb-audio.com/Cable/"},
        "publication": {
            "date": "2026-11-17", "release_tag": "v0.3.2", "original_project_license": "CC-BY-NC-4.0",
            "copyright_notice": "NOTICE",
            "changes": "Counted reusable audio buffers; bounded batched background logging with fault priority; IPC phase timings, worker CPU/memory/thread and DNS48 state-size diagnostics. Retains ATen execution, DPI/tray repairs, connectivity checks, queue-trend recovery, original weights and bundled runtime/driver.",
        },
        "startup_audio_check": {
            "gui_each_primary_startup": True, "requires_microphone_confirmation": True,
            "automatic_microphone_fallback": False, "silent_render": True,
            "mode": "connectivity-only", "performance_thresholds": False,
            "requires_processed_output_submission": True, "audio_stage_timeout_seconds": 45,
            "drops_stale_silent_test_samples": True,
            "driver_endpoint_wait_seconds": 20, "persistent_driver_pending_state": True,
            "text_logs": "%LOCALAPPDATA%/KeepClear/logs",
        },
        "cpu_inference": {
            "backend": "aten", "max_compute_threads": 2, "parallel_min_logical_processors": 4,
            "interop_threads": 1, "flush_denormal": True, "dtype": "float32",
        },
        "diagnostics": {
            "async_log_capacity": 512, "log_batch_size": 32, "max_record_chars": 16384,
            "exit_flush_ms": 2000, "worker_resource_interval_seconds": 5,
            "ipc_phase_timings": True, "worker_cpu_and_state_metrics": True,
            "records_audio": False,
        },
        "runtime_recovery": {
            "mode": "total-queue-trend", "sample_ms": 100, "window_seconds": 1,
            "growth_windows": 2, "minimum_window_growth_ms": 2, "cumulative_growth_ms": 20,
            "growth_queue_ms": 200, "emergency_queue_ms": 500,
            "input_capacity_ms": 512, "output_capacity_ms": 512, "normal_playback_reserve_ms": 48,
            "quiet_span_ms": 20, "quiet_amplitude": 0.00001, "recovery_output_target_ms": 120,
            "recovery_timeout_seconds": None, "cooldown_stop": False,
            "preserve_model_input": True, "may_skip_stale_processed_output": True,
        },
        "files": {p.relative_to(output).as_posix(): sha256(p) for p in sorted(output.rglob("*")) if p.is_file()},
    }
    (output / "PORTABLE.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    size = sum(p.stat().st_size for p in output.rglob("*") if p.is_file())
    print(f"Assembled {output}: {size / 1024**2:.1f} MiB; omitted {omitted_bytes / 1024**3:.2f} GiB of build-only files", flush=True)


def archive_package(output):
    manifest_path = output / "PORTABLE.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    expected = dict(manifest["files"])
    expected["PORTABLE.json"] = sha256(manifest_path)
    names = {p.relative_to(output).as_posix() for p in output.rglob("*") if p.is_file()}
    if names != set(expected):
        raise ValueError("Unlisted/missing files; validate the package before archiving")
    target = Path(str(output) + ".zip")
    temporary = Path(str(target) + ".partial")
    if target.exists() or temporary.exists():
        raise FileExistsError(f"Preserving existing archive: {target}")
    with zipfile.ZipFile(temporary, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as stream:
        for name in sorted(expected):
            if sha256(output / name) != expected[name]:
                raise ValueError(f"Package changed after validation: {name}")
            stream.write(output / name, output.name + "/" + name)
    # Verify actual archived bytes, including every model and native DLL.
    with zipfile.ZipFile(temporary) as stream:
        for name, digest in expected.items():
            with stream.open(output.name + "/" + name) as source:
                if hashlib.file_digest(source, "sha256").hexdigest() != digest:
                    raise ValueError(f"Archived file mismatch: {name}")
    os.rename(temporary, target)
    digest = sha256(target)
    Path(str(target) + ".sha256").write_text(digest + "  " + target.name + "\n", encoding="ascii")
    print(f"Verified release ZIP: {target}; {target.stat().st_size / 1024**2:.1f} MiB; SHA256 {digest}", flush=True)


if __name__ == "__main__":
    main()
