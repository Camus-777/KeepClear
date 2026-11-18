# Changelog

## v0.3.4 — 2026-11-18

- Keep production PCM in float32 NumPy arrays through binary IPC and model stages, reducing float-list conversion and tensor-copy overhead without changing model weights or pipeline order.
- Require three settled low-queue windows before backlog recovery completes; stable 70–80 ms queues and brief 120 ms peaks continue.
- Replenish automatic-retry credit only after 120 continuously observed healthy seconds with capture, model processing and processed playback advancing and no high/growing queue or active recovery.
- Retain the existing one/two-thread CPU policy after local comparison. Startup connectivity checks, device selection and input continuity are unchanged.

## v0.3.3 — 2026-11-17

- Add cancellable automatic audio restart after runtime backlog, input/output callback timeouts and identified model-response watchdog timeouts.
- Require complete cleanup before reopening the exact selected devices; restore monitoring when possible.
- Limit consecutive retries to three with 2/5/15-second backoff; replenish the budget after two healthy minutes and cancel recovery on Stop or Exit.

## v0.3.2 — 2026-11-17

- Set the startup audio connectivity test timeout to 45 seconds.

- Reuse counted audio/resampling buffers and wait arrays; add bounded asynchronous logging with fault priority, IPC phase timings, worker resource and DNS48 state-size diagnostics.

- Center tray Exit text using the current item bounds and scaled padding; regression-check stale text rectangles across DPI scales and enabled/disabled states.

- Optimize DNS48 CPU streaming through ATen with at most two compute threads, one interop thread and denormal flushing. Retain float32, weights, full denoising strength and streaming model parameters. Add CPU backend diagnostics.
- Recalculate native owner-drawn selector item height on handle/font/DPI changes and align Refresh after layout. Normalize the tray Exit menu font/padding on opening, omit unused icon margins and log actual popup metrics. Extend DPI regression checks to selector text height and repeated popup opening.

- Simplify only the startup audio stream test to connectivity: require capture, dual-model output, successful output submission and matching VB-CABLE readback. Remove throughput thresholds and the second performance observation. Ignore timing/underrun/queue fluctuations during that silent test; retain the other startup checks.

- Monitor total queued audio using one-second averages and sustained growth. Stable 70–80 ms and brief 120 ms peaks continue. Prefer quiet-span removal during recovery; reserve processed-output skipping for approximately 500 ms capacity pressure. Remove the one-second recovery failure and 30-second cooldown stop. Preserve model input order and hard resource limits.
- Reuse C#/Python IPC byte buffers and a single per-worker timeout watchdog. Use AboveNormal scheduling for the processing thread and model process; retain MMCSS Audio on device threads.
- Add per-second processing/queue/underrun/discontinuity/GC diagnostics, model-stage timings, session build/CPU identity and DPI layout metrics. Replace the CPU-load assumption in backlog advice with a direct stream-backlog explanation. No microphone audio is logged.
- Establish a 96-DPI design baseline, explicit .NET Framework 4.8 targeting and PerMonitorV2 configuration; scale fixed logical window bounds, startup controls, wordmark/cursor and meters consistently.
- Treat an absent driver-state directory as an already-cleared state.
- Pass 180 C# and 8 Python checks, bilingual UI regressions and 60 simulated DPI/state/language layout checks. Actual remote-PC DPI transitions and long gaming sessions still require retesting.
- Retain the original Alkove Focus → Denoiser DNS48 model order, parameters, weights, third-party runtime and bundled driver archive.

## v0.3.0 — 2026-11-15

### First public KeepClear release

- Publish as KeepClear（保持清晰）without migration from the private development name. Store settings and driver-install state only under KeepClear.
- Prefer the regular verified CABLE Input endpoint for first-use output selection and refresh, while retaining valid saved choices.
- Rebuild the executable and refresh all release hashes; 155 C# and 8 Python checks pass.

### Audio and startup

- Retain the fixed Alkove Focus → Denoiser DNS48 chain, model weights and processing parameters.
- Bundle Python, CPU inference dependencies, model weights and the original base VB-CABLE installer.
- Verify base VB-CABLE using the embedded INF hardware ID/service and actual Windows device topology. Accept healthy driver versions; distinguish missing, incomplete, disabled, busy, failed and restart-required states.
- Require microphone confirmation before each primary GUI startup's silent model/CABLE stream test. Carry the exact input into the main window and keep a disconnected microphone unselected after refresh.
- Allow one bounded recovery observation for transient startup glitches. Persist confirmed setup completion; recover damaged or legacy unconfirmed state without indefinitely blocking repair.

### Interface

- Use a compact fixed-size bilingual interface with smooth meters, local monitoring, single-instance activation and tray operation. Default to English and save language choices.
- Replay the startup wordmark at Run test, with about 2.6 seconds of typing; retain about 1.8 seconds on initial startup.
- Keep closed device selectors white, expanded highlighted rows system blue and keyboard selection available. Ignore wheel selection and omit dotted focus rectangles.
- Show localized error advice with readable layouts, intentional hover details and clickable log access. Ordinary labels have no hover popup.

### Reliability and maintenance

- Isolate per-device query failures while preserving errors linked to the known base driver.
- Keep resources reachable after cleanup timeout, including a retryable system-close path. Handle unreadable settings and disclose save failures.
- Remove unused gain/configuration interfaces, adapter-name metadata, the meter getter and unused latency metrics.
- Historical formatting work preserved syntax and compiled methods at that stage. The current first-release executable is rebuilt after branding and release-selection fixes; its bytes and SHA256 differ from the private maintenance build.

### Distribution

- Original project code, documentation and artwork use CC BY-NC 4.0; retain separate third-party notices and licenses.
- Supply source and Windows release ZIPs, SHA256SUMS.txt and file inventories. Version remains 0.3.0 / 0.3.0.0.

Current test scope is recorded in [validation](docs/VALIDATION.md).
