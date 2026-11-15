"""Validate app-local imports/DLLs and exercise the shipped EXE after relocation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def pe_imports(path):
    data = path.read_bytes()
    pe, = struct.unpack_from("<I", data, 60)
    count, = struct.unpack_from("<H", data, pe + 6)
    optional_size, = struct.unpack_from("<H", data, pe + 20)
    optional = pe + 24
    magic, = struct.unpack_from("<H", data, optional)
    directories = optional + (112 if magic == 0x20b else 96)
    sections = []
    for i in range(count):
        size, va, raw_size, raw = struct.unpack_from("<IIII", data, optional + optional_size + i * 40 + 8)
        sections.append((va, max(size, raw_size), raw))

    def offset(rva):
        for va, size, raw in sections:
            if va <= rva < va + size:
                return raw + rva - va
        return rva

    def name(rva):
        at = offset(rva)
        return data[at:data.index(b"\0", at)].decode("ascii").lower()

    result = set()
    rva, = struct.unpack_from("<I", data, directories + 8)
    if rva:
        at = offset(rva)
        while any(data[at:at + 20]):
            result.add(name(struct.unpack_from("<I", data, at + 12)[0]))
            at += 20
    rva, = struct.unpack_from("<I", data, directories + 13 * 8)
    if rva:
        at = offset(rva)
        while any(data[at:at + 32]):
            result.add(name(struct.unpack_from("<I", data, at + 4)[0]))
            at += 32
    return result


PROBE = r'''
import ctypes
from ctypes import wintypes
import importlib.metadata as metadata
import json,sys,socket,urllib.request
from pathlib import Path
root=Path(sys.argv[1]).resolve()
runtime=root/"runtime"
assert Path(sys.executable).resolve()==runtime/"python.exe"
assert Path(sys.prefix).resolve()==runtime and Path(sys.base_prefix).resolve()==runtime
assert all(Path(p).resolve().is_relative_to(runtime) for p in sys.path),sys.path
assert "site" not in sys.modules
def deny(*args,**kwargs): raise AssertionError("Unexpected network operation")
socket.socket.connect=deny
urllib.request.urlopen=deny
import numpy,torch,alkove
assert (numpy.__version__,torch.__version__,alkove.__version__)==("2.2.6","2.8.0+cpu","0.4.0")
external={name:str(module.__file__) for name,module in tuple(sys.modules.items())
          if name not in {"torch.ops","torch.classes"}  # Torch's synthetic namespaces report placeholder filenames.
          and getattr(module,"__file__",None) and not str(module.__file__).startswith("<")
          and not Path(module.__file__).resolve().is_relative_to(runtime)}
assert not external,external
manifest=json.loads((root/"PORTABLE.json").read_text(encoding="utf-8"))
for name,item in manifest["packages"].items(): assert metadata.version(name)==item["version"]
k=ctypes.WinDLL("kernel32",use_last_error=True)
p=ctypes.WinDLL("psapi",use_last_error=True)
k.GetCurrentProcess.restype=wintypes.HANDLE
k.GetModuleFileNameW.argtypes=[wintypes.HMODULE,wintypes.LPWSTR,wintypes.DWORD]
p.EnumProcessModules.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.HMODULE),wintypes.DWORD,ctypes.POINTER(wintypes.DWORD)]
modules=(wintypes.HMODULE*2048)();needed=wintypes.DWORD()
assert p.EnumProcessModules(k.GetCurrentProcess(),modules,ctypes.sizeof(modules),ctypes.byref(needed))
native={}
for module in modules[:needed.value//ctypes.sizeof(wintypes.HMODULE)]:
    buffer=ctypes.create_unicode_buffer(32768)
    assert k.GetModuleFileNameW(module,buffer,len(buffer))
    path=Path(buffer.value).resolve();name=path.name.lower()
    if name.startswith(("python3","vcruntime140","msvcp140")):
        assert path.is_relative_to(runtime),(name,str(path))
        native[name]=str(path)
for name in ["python312.dll","msvcp140.dll","vcruntime140.dll"]: assert name in native,native
print(json.dumps({"executable":sys.executable,"prefix":sys.prefix,"sys_path":sys.path,
    "native_runtime_modules":native,"package_versions":{name:metadata.version(name) for name in manifest["packages"]},
    "external_module_paths":external,"site_disabled":True},ensure_ascii=True))
'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("package", type=Path)
    parser.add_argument("--report", type=Path, default=ROOT / "artifacts" / "portable-validation.json")
    args = parser.parse_args()
    package = args.package.resolve()
    manifest = json.loads((package / "PORTABLE.json").read_text(encoding="utf-8"))
    expected = manifest["files"]
    actual_files = {p.relative_to(package).as_posix() for p in package.rglob("*") if p.is_file()}
    assert actual_files == set(expected) | {"PORTABLE.json"}, "Unexpected/missing files in release"
    for name, digest in expected.items():
        assert sha256(package / name) == digest, name
    print(f"Verified {len(expected)} shipped files", flush=True)
    dlls = {p.name.lower(): p for p in (package / "runtime").rglob("*") if p.suffix.lower() in {".dll", ".pyd", ".exe"}}
    imports = set()
    for path in dlls.values():
        imports.update(pe_imports(path))
    system = Path(os.environ["WINDIR"]) / "System32"
    unresolved = []
    system_dependencies = []
    for name in sorted(imports):
        if name in dlls or name.startswith(("api-ms-", "ext-ms-")):
            continue
        if name.startswith(("msvcp", "vcruntime", "concrt", "python")) or not (system / name).exists():
            unresolved.append(name)
        else:
            system_dependencies.append(name)
    assert not unresolved, unresolved
    sandbox_parent = ROOT / "artifacts" / "portable-relocation"
    sandbox_parent.mkdir(parents=True, exist_ok=True)
    # The directory is intentionally left for review; no existing files are removed.
    sandbox = Path(tempfile.mkdtemp(prefix="独立包 空格 ", dir=sandbox_parent)).resolve()
    moved = sandbox / "KeepClear moved"
    shutil.copytree(package, moved)
    trap = sandbox / "invalid-python"
    trap.mkdir()
    marker = trap / "HOST-PYTHON-WAS-USED.txt"
    (trap / "sitecustomize.py").write_text(f"from pathlib import Path\nPath({str(marker)!r}).write_text('unexpected site import')\nraise RuntimeError('Host Python path leaked')\n")
    environment = os.environ.copy()
    environment.update({"PATH": str(system), "PYTHONHOME": str(trap), "PYTHONPATH": str(trap),
                        "PYTHONUSERBASE": str(trap), "PYTHONUTF8": "1", "HTTP_PROXY": "http://127.0.0.1:9", "HTTPS_PROXY": "http://127.0.0.1:9"})
    creation = subprocess.CREATE_NO_WINDOW
    probe = subprocess.run([str(moved / "runtime" / "python.exe"), "-I", "-B", "-c", PROBE, str(moved)],
                           env=environment, cwd=trap, capture_output=True, timeout=60, creationflags=creation)
    if probe.returncode:
        raise RuntimeError(probe.stderr.decode("utf-8", errors="replace"))
    runtime_report = json.loads(probe.stdout)
    assert not marker.exists()
    report_path = sandbox / "self-test.txt"
    started = time.perf_counter()
    tested = subprocess.run([str(moved / "KeepClear.exe"), "--self-test", str(report_path)],
                            env=environment, cwd=trap, capture_output=True, timeout=90, creationflags=creation)
    text = report_path.read_text(encoding="utf-8-sig") if report_path.exists() else tested.stderr.decode(errors="replace")
    if tested.returncode or "PASS;" not in text or str(moved / "runtime" / "python.exe") not in text:
        raise RuntimeError(text)
    assert not marker.exists()
    startup_path=sandbox / "startup-check.txt"
    startup=subprocess.run([str(moved / "KeepClear.exe"),"--check-startup",str(startup_path)],
                           env=environment,cwd=trap,capture_output=True,timeout=90,creationflags=creation)
    startup_text=startup_path.read_text(encoding="utf-8-sig") if startup_path.exists() else ""
    if startup.returncode not in (0,1) or not any("status="+value in startup_text for value in ["ready","missing-driver","inactive-driver","incomplete-driver","restart-required","driver-detection-failed","driver-pending"]):
        raise RuntimeError("Read-only startup check failed: "+startup_text)
    report = {
        "result": "PASS", "file_count": len(expected), "package": str(package), "relocated_package": str(moved),
        "total_bytes": sum(p.stat().st_size for p in package.rglob("*") if p.is_file()),
        "host_path_removed": True, "host_python_environment_poisoned": True, "sitecustomize_trap_triggered": False,
        "runtime": runtime_report, "native_dll_imports": len(imports), "windows_system_dependencies": system_dependencies,
        "exe_self_test": text, "exe_self_test_wall_seconds": time.perf_counter() - started,
        "read_only_startup_check": startup_text,
        "limits": "Local controlled relocation test; not a fresh Windows machine; no microphone/audio devices opened.",
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(text, flush=True)
    print(f"PASS: relocated app imports, native runtimes and shipped EXE dual-model self-test; report={args.report}", flush=True)


if __name__ == "__main__":
    main()
