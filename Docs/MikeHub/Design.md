# SmartMike Hub — application design

Goal: a simple app that connects to **multiple SmartMike+ mics at once**, exposes each as a **Windows input device**, and records them **simultaneously** with one Start/Stop. Runs on Windows and **Android**.

## Constraints
- Native on **Windows ARM64 and x64**, and **Android** (arm64 phones, x64 emulator).
- Portable later to **macOS and Linux** — we don't build those now, but nothing may block them.
- Stack: **C# / .NET 10**, UI in **Avalonia** (Windows, macOS, Linux, Android, iOS).

## Architecture

```
┌──────────── SmartMike.App (shared Avalonia UI) + heads: .Desktop, .Android ─────────┐
│ device list · per-mic status/battery/level · settings · output mapping · Record All │
└──────────────────────────────────────┬───────────────────────────────────────────────┘
                                       │ (plain .NET API, no UI types)
┌──────────────────────────── SmartMike.Core (net10.0, no OS APIs) ───────────────────┐
│ Protocol: frames, commands, TLVs, reports      MikeSession: handshake, clock sync,  │
│ QCELT header + CELT 0.7.1 (P/Invoke)           AUDIO_BEGIN/END, heartbeat, settings │
│ JitterBuffer (drift-compensated resampling)    MikeHub: N sessions, Record All       │
│ WavRecorder                                    Interfaces: IRfcommConnector,         │
│                                                IDeviceDiscovery, IAudioSinkFactory   │
└──────────────┬─────────────────────────────────────────────┬────────────────────────┘
               │                                             │
┌──────────────▼───────────── platform implementations ──────▼────────────────────────┐
│ SmartMike.Platform.Windows   RFCOMM: Winsock AF_BTH (connect by SPP UUID)            │
│ (net10.0-windows)            Discovery: paired devices (registry / BluetoothAPIs)    │
│                              Audio sink: WASAPI render → virtual cable endpoint      │
│ SmartMike.Platform.Android   RFCOMM: BluetoothSocket (SPP UUID) · bonded devices     │
│ (net10.0-android)            Audio sink: AudioTrack (float, low latency) → monitor   │
│ later: Platform.Linux        RFCOMM: AF_BLUETOOTH socket · sink: PipeWire source     │
│ later: Platform.MacOS        RFCOMM: IOBluetooth helper · sink: AudioServerPlugIn    │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

- **Core has no OS dependencies** — runs on any .NET 10 RID and is unit-tested against real captures.
- **Native CELT**: `Native/celt/runtimes/<rid>/native/celt071.{dll,so,dylib}`, built from the stock CELT 0.7.1 source: `win-x64`, `win-arm64` (llvm-mingw), `android-arm64`, `android-x64` (NDK r29). Loaded with `[LibraryImport("celt071")]`; on Android the APK carries it as `lib/<abi>/libcelt071.so`.
- **App heads** supply an `IAppHost` (hub factory, recording folder, permission prompt, open/share recordings, keep-awake). The UI lives in `MainView` (a `UserControl`) so desktop wraps it in a window and Android shows it directly.
- **SmartMike.Cli** (`net10.0`): headless runner for testing and future Linux/macOS service use.

## Virtual microphones (Windows)
Pluggable `IAudioSink`:
1. **v1** — render decoded PCM to an existing virtual cable's playback side (e.g. VB-CABLE "CABLE Input"; ARM64 supported since Oct 2024). The user picks one cable per mic in the UI. Apps record from the cable's capture side.
2. **later** — own ACX virtual-audio driver exposing one capture endpoint per SmartMike (named after the mic). Needs Microsoft attestation signing.

macOS: AudioServerPlugIn (BlackHole-style). Linux: PipeWire virtual source per mic (no driver).

## Android
- Lists **paired** SmartMikes (pair in Android Bluetooth settings first); no scanning, so only `BLUETOOTH_CONNECT` is requested (Android 12+).
- Each mic's output picker routes live audio to the phone speaker, wired/USB headphones or a Bluetooth output (`AudioTrack.SetPreferredDevice`). There is no "virtual input device" on Android.
- Recordings go to app storage (`Android/data/com.zealofzebras.smartmikehub/files/Recordings/<timestamp>/`, reachable over USB). **Share recordings** sends the last take's WAVs through the share sheet (FileProvider).
- The screen stays on while recording. Recording with the screen off needs a foreground service (later).
- min Android 8.0 (API 26); 16 KB-page-aligned native lib for Android 15+.

## Recording
Record All / Stop All starts every connected mic's `WavRecorder` at the same wall-clock instant. Files: `Recordings/<timestamp>/<mic-name>.wav`, 44.1 kHz mono 16-bit (the capsule is mono; L = R). Recording works with or without a virtual cable.

## Multiple mics
Each SmartMike has its own RFCOMM link and `MikeSession`. One stream ≈ 135 kbit/s; a BR/EDR adapter should carry several. Exact limit to be measured.

## Project layout
```
Src/
  SmartMike.slnx
  SmartMike.Core/               protocol, session, codec, jitter buffer, recorder
  SmartMike.Core.Tests/         xUnit — frames, headers, decode of Captures/*
  SmartMike.Platform.Windows/   RFCOMM, discovery, WASAPI sink
  SmartMike.Platform.Android/   RFCOMM (BluetoothSocket), bonded devices, AudioTrack sink
  SmartMike.Cli/                headless runner
  SmartMike.App/                shared Avalonia UI (MainView, view models, IAppHost)
  SmartMike.App.Desktop/        desktop head → SmartMikeHub.exe
  SmartMike.App.Android/        Android head → com.zealofzebras.smartmikehub.apk
Native/celt/                    per-RID CELT binaries
```

## Status (2026-10-11)
| Part | State |
|---|---|
| `SmartMike.Core` | Done: protocol, session (connect/handshake/sync/audio/settings/reconnect), QCELT + CELT P/Invoke, drift-compensated jitter buffer, WAV recorder, hub with Record All |
| `SmartMike.Core.Tests` | 10 tests pass on **win-arm64 and win-x64** (incl. decoding a real capture, matching the Python decoder within ±2 LSB) |
| `SmartMike.Platform.Windows` | Done: RFCOMM via `Socket(AF_BTH)` + SPP UUID, paired-device list from registry, WASAPI output (NAudio 3.1 `WasapiPlayer`) |
| `SmartMike.Cli` | `list`, `outputs`, `run [--output X] [--record N]` — verified on hardware: connect, stream, record (440 frames, 0 errors), live output (0 errors) |
| `SmartMike.App` | First version: mic cards (state, FW, battery, level meter, connect, output picker, gain/AGC, monitor, noise suppression), Record All / Stop, recordings folder |
| `SmartMike.App.Desktop` | Runs on Windows (same UI, now hosted as `MainView`) |
| `SmartMike.App.Android` | Builds; Debug and Release APKs (arm64 + x64) with CELT included. **Not yet run on a phone** (no device attached; the Android emulator does not run on Windows ARM64 hosts) |
| Virtual cable | Not installed yet; the output picker lists cables first when present |

### Build notes
- Native CELT: `bash Native/celt/build-windows.sh` (llvm-mingw 20240619). Zig 0.13's aarch64-windows DLLs fail to load ("incorrect format"); don't use them.
- `dotnet test Src/SmartMike.Core.Tests` (ARM64) and `dotnet test Src/SmartMike.Core.Tests --arch x64` both pass.
- Android CELT: `bash Native/celt/build-android.sh` (NDK r29 in `%LOCALAPPDATA%\Android\Sdk\ndk`, installed with `sdkmanager --sdk_root=%LOCALAPPDATA%\Android\Sdk "ndk;29.0.14206865"`; `sdkmanager.bat` needs a `JAVA_HOME` without parentheses, e.g. the Microsoft JDK).
- Desktop: `dotnet run --project Src/SmartMike.App.Desktop`. Windows-only code is marked `[SupportedOSPlatform("windows")]` and chosen in `SmartMike.App.Desktop/DesktopHost.cs`.
- Android: `dotnet publish Src/SmartMike.App.Android -c Release` → `bin/Release/net10.0-android/publish/com.zealofzebras.smartmikehub-Signed.apk` (debug-key signed, for sideloading). With a phone on USB debugging: `dotnet build Src/SmartMike.App.Android -t:Install` or `adb install -r <apk>`.
