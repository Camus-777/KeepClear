# KeepClear v0.3.4 validation

Prepared 2026-11-18. This repair release addresses runtime queue trends, startup connectivity and consistent DPI layout. It is tested locally without microphone capture or driver installation.

| Check | Result |
| --- | --- |
| Native C# checks | 243 passed, including stable-queue retry-credit and three-window recovery-completion regression checks, 27 original automatic-restart checks and 22 buffer ownership/resampling and bounded asynchronous logging checks |
| Python worker checks | 16 passed |
| Startup audio connectivity | Capture, dual-model output, successful processed-output submission and matching CABLE readback are required; timing/underrun/queue metrics do not gate entry; genuine faults still block |
| Runtime recovery | Stable 70–80 ms and brief 120 ms peaks continue; slow/fast sustained growth triggers recovery; 350 ms stalls retain all simulated voice samples; quiet-span trimming preserves voiced order; true input exhaustion remains bounded |
| CPU inference repair | ATen execution with up to 2 compute threads and denormal flushing; synthetic tone/silence/tiny/decay profiles and full-chain mixed synthetic speech compared numerically; local mean 8.61 to 4.47 ms per block, P99 10.17 to 6.24 ms; reported Ryzen H 260 still requires retesting |
| Buffer reuse and diagnostics | Counted reusable buffers, old/new compiled resampler exact comparison, bounded nonblocking logging, critical-record priority, flush timeout and disk-error isolation; IPC send/wait/receive, worker CPU/memory/threads and DNS48 state metrics |
| Automatic restart | Main-form injected sessions verify exact devices, cleanup-before-start, retry budget/backoff, healthy reset, Stop/Exit cancellation, retained failed cleanup and monitor fault isolation |
| Protocol and lifecycle | Exact PCM round trips, repeated packets, cancellation and bounded stalled-worker timeout retained |
| Main/startup/choice UI | English and Simplified Chinese fixtures check fixed bounds, selectors, monitoring, tray/instance behavior, microphone handoff and animation replay |
| DPI layout | 72 simulated layout checks across 96/120/144/168/192 DPI and return to 96, both languages, main/automatic-restart/selection/restart/pending/error states; selector text/item heights and refresh alignment checked; tray font drift is corrected across repeated openings and the actual popup is rendered at the host DPI, logged separately |
| Audio processing | Fixed Alkove Focus → Denoiser DNS48, 16 kHz mono, 256-sample blocks, original weights and model parameters retained |
| Driver and configuration | Original embedded installer and hardware/service identity rules retained; expected absence of the state directory no longer logs an error |

Release packaging separately verifies every manifest entry, archive member, model/runtime/driver hash, isolated runtime relocation, native DLL origins and synthetic processing through the shipped C# → Python → dual-model path. The local material report records the measured results and release hashes.

DPI fixtures simulate dimensions and font scaling; they do not replace real multi-monitor/remote-desktop DPI transitions. Synthetic audio is not speech-quality evaluation, microphone capture or a game-load test. The two user-reported PCs, fresh-PC driver installation and long-duration gaming remain to be retested. No driver setup, Windows restart or GitHub upload is performed. The executable is unsigned.

Recovery behavior is documented in [REPAIR-0.3.4.md](REPAIR-0.3.4.md); first-release settings and output selection remain as described in [RENAMING.md](RENAMING.md).

## v0.3.4 scope

The production worker keeps PCM in float32 NumPy arrays through model stages and binary transport, avoiding Python float-list roundtrips and tensor copies. A local 1/2-thread comparison retains the existing two-thread policy; no affinity, realtime priority or per-start performance selection is introduced. Recovery completion requires three consecutive non-growing one-second windows with mean queue <=160 ms and peak <200 ms. Retry credit requires 120 continuously observed healthy seconds, all three stages advancing, no active recovery and total queue <200 ms; sustained growth and observation gaps break continuity. Isolated underruns are not a failure criterion.

Full benchmark, output-equivalence and package-integrity measurements are recorded in the separate material verification report. Local synthetic CPU-load tests do not establish improved stability on the reporting PC or while gaming.
