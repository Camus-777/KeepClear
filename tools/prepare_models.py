"""Explicit installation-time downloads. The application itself stays offline."""
import hashlib
import importlib.metadata
import json
from pathlib import Path
import shutil
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DNS_URL = "https://dl.fbaipublicfiles.com/adiyoss/denoiser/dns48-11decc9d8e3f0998.th"
DNS_SHA = "11decc9d8e3f0998a64caed6b57b68a40cb200a627996a636625f20bdb91f1d6"


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for part in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(part)
    return digest.hexdigest()


def main():
    import alkove
    import torch
    if alkove.__version__ != "0.4.0":
        raise RuntimeError("This integration is pinned to alkove 0.4.0")
    upstream_path = ROOT / "third_party" / "denoiser" / "UPSTREAM.json"
    upstream = json.loads(upstream_path.read_text(encoding="utf-8"))
    for name, entry in upstream["files"].items():
        if sha256(upstream_path.parent / name) != entry["sha256"]:
            raise RuntimeError("Denoiser upstream source checksum mismatch: " + name)
    models = ROOT / "models"
    focus = alkove.MANIFEST["models"]["focus"]
    for name in ("focus", "cue"):
        spec = alkove.MANIFEST["models"][name]
        target = models / "alkove" / spec["file"]
        target.parent.mkdir(parents=True, exist_ok=True)
        if not target.exists():
            cached = alkove.fetch([name], cache=ROOT / "artifacts" / "alkove-cache")[name]
            shutil.copyfile(cached, target)
        if sha256(target) != spec["sha256"]:
            raise RuntimeError("Alkove " + name + " checksum mismatch")
    dns = models / "denoiser" / "dns48-state.pt"
    dns.parent.mkdir(parents=True, exist_ok=True)
    if not dns.exists():
        with urllib.request.urlopen(DNS_URL, timeout=60) as response:
            contents = response.read()
        if hashlib.sha256(contents).hexdigest() != DNS_SHA:
            raise RuntimeError("DNS48 download checksum mismatch")
        dns.write_bytes(contents)
    if sha256(dns) != DNS_SHA:
        raise RuntimeError("DNS48 checksum mismatch")
    state = torch.load(dns, map_location="cpu", weights_only=True)
    if not isinstance(state, dict) or not state or not all(isinstance(v, torch.Tensor) for v in state.values()):
        raise RuntimeError("DNS48 must contain a tensor-only state_dict")
    manifest = {
        "schema": 1, "sample_rate": 16000, "block_samples": 256,
        "order": ["alkove-focus", "denoiser-dns48"],
        "packages": {name: importlib.metadata.version(name) for name in ("alkove", "numpy", "torch")},
        "alkove": {"file": "alkove/" + focus["file"], "sha256": focus["sha256"], "upstream": focus},
        "cue": {"file": "alkove/" + alkove.MANIFEST["models"]["cue"]["file"],
                "sha256": alkove.MANIFEST["models"]["cue"]["sha256"],
                "upstream": alkove.MANIFEST["models"]["cue"], "reason": "Alkove 0.4.0 Processor dependency; gate disabled"},
        "denoiser": {"file": "denoiser/dns48-state.pt", "sha256": DNS_SHA, "url": DNS_URL,
                     "commit": "8afd7c166699bb3c8b2d95b6dd706f71e1075df0", "license": "CC-BY-NC-4.0"},
    }
    (models / "MANIFEST.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    licenses = ROOT / "third_party" / "alkove"
    licenses.mkdir(parents=True, exist_ok=True)
    distribution = importlib.metadata.distribution("alkove")
    for file in distribution.files:
        if str(file).replace("\\", "/").endswith(("licenses/LICENSE", "licenses/NOTICE")):
            shutil.copyfile(distribution.locate_file(file), licenses / Path(str(file)).name)
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
