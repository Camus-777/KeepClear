# Validation snapshot: v0.3.0

Prepared 2026-11-15. Local results, with target-PC installation still to be verified.

| Check | Result |
| --- | --- |
| Basic tests | 147 C# and 8 Python passed after formatting |
| Source formatting | 26 first-party C# files retain exact syntax structure, token text, strings and comments |
| Compiled comparison | 88 types, 414 fields, 383 methods; signatures, IL, locals, exception handlers and embedded resources unchanged; compiler-generated private type GUID normalized |
| Distributed binary | Original maintenance EXE retained byte-for-byte, with the same SHA256 |
| Explicit microphone | Confirmation required; missing selection never falls back; failed test permits selection/retry; exact ID persists into main window |
| Preference write failure | Current selected microphone remains usable; failed persistence is disclosed |
| Driver scope | Unrelated endpoint metadata errors do not block repair; errors linked to the base driver remain distinct |
| Setup state | Confirmed successful attempts persist; malformed/locked/unreadable state is nonfatal; legacy unconfirmed attempts receive bounded observation before repair; active installers and confirmed pending attempts are not repeated |
| System exit | Simulated cleanup timeout restores a retryable window; a second close completes cleanup; preference-save errors are logged without escaping |
| Error actions/layout | All localized error categories fit the main hint; error clicks use an injected log opener; normal labels retain no action or tooltip |
| Refresh | A missing confirmed microphone remains unselected across refreshes; explicit replacements are retained |
| Transient recovery | One underrun enters one recovery window; stable recovery passes; repeated failure stops |
| Error localization | Permission, busy, disconnected, unavailable mic, model load/process, throughput, timeout, readback and format titles/actions in both languages |
| Animation replay | A fast fixture check cannot bypass the full 2.6-second replay; compact selector/refresh/actions align in both languages |
| Selector/hover | White focused closed fields, system-blue highlighted list rows without dotted focus rectangles, ordinary text without hover popups, intentional error details retained; native bitmap and tooltip mapping assertions |
| Native UI | Main size, wheel suppression, monitor gating, tray, language persistence; startup selector, guidance and error layouts; cross-thread UI access checks in selector tests |
| Real startup | Carried forward from the preceding identical maintenance EXE: confirmed microphone → silent models/CABLE → same main input. No microphone session repeated for formatting |
| Portable | Host Python environment isolated, package moved to Chinese/space paths; imports, native runtimes and shipped EXE synthetic models checked |

The normal observation is 2.5 seconds after models load. Recovery adds 0.5 seconds of settling and one more 2.5-second observation only when needed. This is a silent stream/throughput check; it does not correlate a nonzero signal through VB-CABLE or measure subjective voice quality/end-to-end game latency.

Installer/UAC/reboot paths use injected callbacks and isolated state files; the developer machine was not reinstalled or rebooted. No fresh-Windows or every-device guarantee is claimed. Public materials contain sanitized results, not local device IDs, raw logs or microphone recordings. Earlier performance figures are grouped in [historical validation](HISTORICAL-VALIDATION.md). Formatting details are recorded in [FORMATTING.md](FORMATTING.md).
