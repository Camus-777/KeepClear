# KeepClear v0.3.0 validation

Prepared 2026-11-15 for the first public KeepClear（保持清晰）release. The executable is rebuilt after the two release-review findings were addressed under the first-release scope.

| Check | Result |
| --- | --- |
| Native C# checks | 155 passed: the 147-check baseline plus 8 output-selection regressions; the 4 development-name compatibility checks were removed with that compatibility code |
| Python worker checks | 8 passed |
| First-release settings/state | KeepClear reads only its own settings and installation state, uses its own single-instance and installation locks, and performs no development-name migration |
| Output selection | Regular verified CABLE Input preferred on first use or missing saved output, including renamed endpoints; valid saved choices retained; optional metadata failure and single-endpoint fallback covered |
| Live endpoint metadata | Read-only local enumeration confirmed regular CABLE Input is Speakers, CABLE In 16ch is LineLevel, and the regular endpoint is selected without opening an audio stream |
| Main/startup/choice interfaces | English and Simplified Chinese fixtures rerun for fixed dimensions, selectors, monitor availability, tray/instance behavior, microphone handoff and animation replay |
| Audio processing | Alkove Focus → Denoiser DNS48, 16 kHz mono, 256-sample blocks, model parameters and original weights retained |
| Driver | Original embedded VB-CABLE installer unchanged; hardware/service identity remains required; optional endpoint type is used only to rank already verified outputs |

Release packaging separately verifies the actual executable, isolated runtime relocation, native DLL origins, synthetic dual-model processing, all archive members and SHA256 manifests. The local material verification report records these results. No microphone capture, driver installation, OS restart or GitHub upload is performed. Fresh-PC installation, subjective audio quality and long-duration gaming remain unverified. The executable is unsigned.

First-release configuration and output-selection rules are documented in [RENAMING.md](RENAMING.md).
