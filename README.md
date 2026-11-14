<p align="right">
  <a href="README.zh-CN.md" title="Switch to Simplified Chinese">
    <img src="assets/language-switch-zh.svg" width="104" height="32" alt="Switch to Simplified Chinese">
  </a>
</p>

<p align="center">
  <img src="assets/KeepQuiet.png" width="80" alt="KeepQuiet sound-wave icon">
</p>

<h1 align="center">KeepQuiet</h1>

<p align="center">
  <b>Local Real-Time Voice Isolation &amp; Noise Suppression for Windows Games and Calls</b><br>
  <sub>面向 Windows 游戏和通话的本地实时人声分离与降噪工具</sub>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Version-0.3.0-007ec6" height="20" alt="Version 0.3.0">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%2F11%20x64-007ec6" height="20" alt="Windows 10/11 x64">
  <img src="https://img.shields.io/badge/Audio-WASAPI%2016kHz-4c1" height="20" alt="WASAPI 16kHz">
  <img src="https://img.shields.io/badge/Checks-147%20C%23%20%2B%208%20Python%20Passed-4c1" height="20" alt="Tests Passed">
  <img src="https://img.shields.io/badge/License-CC%20BY--NC%204.0-9f9f9f" height="20" alt="License CC BY-NC 4.0">
</p>

<p align="center"><img src="https://img.shields.io/badge/%E8%AF%AD%E8%A8%80-English%2F%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-007ec6?labelColor=555" height="20" alt="语言: English/简体中文"></p>

---

## Overview

**KeepQuiet** is an offline, real-time voice isolation and noise suppression desktop utility for Windows 10/11 x64. It processes live microphone audio on the local CPU through a fixed two-stage neural pipeline and routes the cleaned speech to a virtual audio cable for games, voice chats, and calls:

```text
Microphone (WASAPI) ──► Alkove Focus (Voice Isolation) ──► Denoiser DNS48 (Noise Suppression) ──► VB-CABLE
```

All inference runs **100% locally and offline**. KeepQuiet does not save microphone audio to disk or upload it. The portable release bundles Python, CPU inference dependencies, Focus/Cue/DNS48 weights, and the original VB-CABLE installer—no separate Python, CUDA, or driver download is needed.

---

## Key Features

- **Fixed Serial Dual-Model Pipeline**: Chains `Alkove Focus` and `Denoiser DNS48` at `16 kHz` mono (`256 samples/block`), with no dry/wet blend or silent fallback to raw audio.
- **Native Windows WASAPI Audio**: Direct event-driven WASAPI capture and playback with no external audio/UI framework, housed in a compact fixed-size window with smooth input/output meters and independent local headphone monitoring.
- **Confirmed Microphone & Silent Startup Test**: Each time you launch the main application, you are asked to confirm an active, unmuted microphone before a real silent dual-model/CABLE stream test runs. A missing selection is never replaced automatically.
- **Smart VB-CABLE Detection & Repair**: Identifies VB-CABLE using the hardware ID and service specified in the bundled INF, verified through Windows endpoint topology; requests one bundled setup attempt when missing or incomplete, and persists pending/restart state to avoid repeated installations.
- **Resilient Diagnostics & Recovery**: Includes a bounded recovery observation for brief startup glitches, isolates unrelated endpoint query errors, and provides localized EN/ZH troubleshooting advice with clickable diagnostic log access (`LocalAppData/KeepQuiet/logs`).
- **Bilingual Tray Operation**: Instant **English/简体中文** switching (saved across sessions), single-instance protection, and background tray operation (left-click to restore, right-click and select Exit to quit).

---

## Download & Quick Start

Download **`KeepQuiet-0.3.0-win-x64.zip`** from the **[Releases](../../releases)** page, extract the entire folder, and launch `KeepQuiet.exe` (requires `.NET Framework 4.8`).

1. **Confirm & Test Microphone**: At startup, select your microphone, ensure it is on/unmuted, and click **Run test**. After the test passes, keep **CABLE Input** selected as the virtual output in the main window.
2. **Start Processing**: Click **Start**, then set **CABLE Output** as your microphone in your game or calling app.
3. **Local Monitoring (Optional)**: Select your headphones and enable **Monitor** after processing starts to hear the processed voice stream.

---

## Build from Source

Requires **Windows x64 CPython 3.12.10** and **.NET Framework 4.8** (no Visual Studio, .NET SDK, NuGet, or Node.js required):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./setup-models.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File ./build.ps1 -Test
./dist/KeepQuiet.Tests.exe --ui ./artifacts/ui
./dist/KeepQuiet.Tests.exe --startup-ui ./artifacts/startup-ui
powershell -NoProfile -ExecutionPolicy Bypass -File ./build-portable.ps1
```


---

## License & Third Parties

- Original KeepQuiet code, documentation, and artwork are licensed under **[CC BY-NC 4.0](LICENSE)**. See [NOTICE](NOTICE) and [License Scope](LICENSING.md).
- Third-party components retain their respective terms (including Denoiser under CC BY-NC 4.0). See [THIRD-PARTY.md](THIRD-PARTY.md).
- VB-CABLE is donationware by [VB-Audio](https://www.vb-cable.com/). [Support its author](https://shop.vb-audio.com/en/win-apps/11-vb-cable.html).
