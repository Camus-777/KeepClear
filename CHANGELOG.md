# Changelog

## v0.3.0 — 2026-11-15

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
- Consolidate documentation and format first-party C# without changing tokens, strings, comments, method structure or runtime behavior. The distributed EXE remains byte-for-byte identical to the preceding maintenance build.

### Distribution

- Original project code, documentation and artwork use CC BY-NC 4.0; retain separate third-party notices and licenses.
- Supply source and Windows release ZIPs, SHA256SUMS.txt and file inventories. Version remains 0.3.0 / 0.3.0.0.

Current test scope is recorded in [validation](docs/VALIDATION.md). Earlier local measurements are retained in [historical validation](docs/HISTORICAL-VALIDATION.md).
