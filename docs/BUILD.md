# Building KeepClear 0.3.3

Use Windows 10/11 x64, CPython 3.12.10 x64 and .NET Framework 4.8.
Run commands at the repository root in Windows PowerShell. The .NET Framework
compiler is used directly; no third-party build framework is required.

1. `powershell -NoProfile -ExecutionPolicy Bypass -File ./setup-models.ps1`
   creates `.venv`, installs the hash-locked wheels, downloads verified weights
   and builds development output. To select Python explicitly, pass `-Python`
   followed by the path to the CPython executable.
2. `powershell -NoProfile -ExecutionPolicy Bypass -File ./build.ps1 -Test`
   runs the C# and Python basic checks. The Python command must resolve to CPython 3.12.
3. `./dist/KeepClear.Tests.exe --ui ./artifacts/ui` checks UI/tray behavior.
   `./dist/KeepClear.Tests.exe --startup-ui ./artifacts/startup-ui` checks startup states.
   `./dist/KeepClear.Tests.exe --dpi-ui ./artifacts/dpi-ui` simulates DPI layouts.
   These checks use fixtures and do not install drivers, restart Windows or save user preferences.
4. `powershell -NoProfile -ExecutionPolicy Bypass -File ./build-portable.ps1`
   assembles the runtime, validates relocation and model processing, then creates
   `release/KeepClear-0.3.3-win-x64.zip` and its SHA256 sidecar. Existing output is preserved;
   use a new output path within `release` for a repeat build.

Models and Python are not committed to Git. The source archive alone is not the portable release.
The original base VB-CABLE ZIP in `drivers` is required by the compiler's embedded resource.
See [model integration](MODEL-INTEGRATION.md) and [portable details](PORTABLE.md).

Optional `--hardware-models`, `--monitor-engine` and `--tray-hardware` checks open
real devices. Run them deliberately with VB-CABLE installed and headphones selected.
They do not save microphone recordings. `tools/validate_models.py` additionally
requires a separately supplied synthetic speech WAV at `artifacts/fixtures/tts-clean.wav`.
No private audio fixture is included in the repository.

The publication build scripts retain LICENSE, NOTICE, LICENSING.md, bilingual
README files and assets in rebuilt releases. Third-party license texts remain
in third_party and the runtime package metadata.

`--startup-choice` runs the isolated microphone chooser UI tests. `--startup-choice-hardware` explicitly opens the real silent chain and checks the exact microphone handoff; run it deliberately from a complete runtime directory. It does not install drivers or change real preferences.
