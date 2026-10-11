# SmartMike Hub — project guide

Reverse-engineered Sabinetek SmartMike+ wireless mics (discontinued; MetAudio app) and our own app
that streams several mics at once. Apps: **Windows desktop** and **Android**, one shared Avalonia UI.
Docs/ is the source of truth — read `Docs/MikeHub/Design.md` (app) or `Docs/Protocol/*` (protocol) before changing related code.

## Hard rules
- **Never install, flash or send firmware to a device.** Never build CSR DFU parsers/extractors.
- After hardware tests, restore device settings: monitor volume 80, AGC on, mic gain 50.
- Core (`SmartMike.Core`) has no OS APIs — platform code goes in `SmartMike.Platform.*` or an app head.
- Everything must work natively on win-arm64 **and** win-x64, android-arm64 (phones) and android-x64 (emulator). Don't block later macOS/Linux ports.

## Layout
```
Src/SmartMike.slnx
  SmartMike.Core/               protocol (Swiss SPP frames), MikeSession, MikeHub, QCELT/CELT decode, JitterBuffer, WavWriter
  SmartMike.Core.Tests/         xUnit, incl. decoding a real capture (TestData/)
  SmartMike.Platform.Windows/   AF_BTH RFCOMM socket, paired devices from registry, WASAPI sink (NAudio 3.1)
  SmartMike.Platform.Android/   BluetoothSocket RFCOMM, bonded devices, AudioTrack sink
  SmartMike.App/                shared UI: Views/MainView.axaml, ViewModels/, Services/IAppHost.cs
  SmartMike.App.Desktop/        desktop head → SmartMikeHub.exe (DesktopHost)
  SmartMike.App.Android/        Android head → com.zealofzebras.smartmikehub (AndroidHost, MainActivity)
  SmartMike.Cli/                headless runner: list | outputs | run [--output X] [--record N]
Native/celt/                    CELT 0.7.1 per RID; build-windows.sh (llvm-mingw), build-android.sh (NDK r29)
Tools/                          Python reference tools (swiss_protocol.py, smartmike_client.py, qcelt_decode.py)
App/Decompiled/                 decompiled MetAudio APK (reference only; large — search, don't read whole trees)
Docs/                           App/, Firmware/, Protocol/, MikeHub/Design.md, Plan.md
```

## Stack
.NET 10 SDK 10.0.301 · Avalonia 12.1.4 (compiled bindings, `x:DataType`) · CommunityToolkit.Mvvm 8.4.2
(`[ObservableProperty] public partial …`, `[RelayCommand]`) · NAudio.Wasapi 3.1.0 · Android min API 26.

## Commands (run from `Src/`)
- Tests: `dotnet test SmartMike.Core.Tests` and `dotnet test SmartMike.Core.Tests --arch x64`
- Desktop: `dotnet build SmartMike.App.Desktop` / `dotnet run --project SmartMike.App.Desktop`
- Android: `dotnet build SmartMike.App.Android` · release APK: `dotnet publish SmartMike.App.Android -c Release`
  → `SmartMike.App.Android/bin/Release/net10.0-android/publish/com.zealofzebras.smartmikehub-Signed.apk`
- adb: `"C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe"` (install `-r`, `logcat`)
- sdkmanager needs `JAVA_HOME=C:\Program Files\Microsoft\jdk-21.0.8.9-hotspot` (path without parentheses).
- Host is Windows ARM64: the Android emulator can't run here, so test on a real phone over USB.

## Device facts
SmartMike+ = CSR8670, MAC prefix 28:36:38 / 12:FC:08, FW 0.4.37. SPP UUID 00001101-…; frames `FF 5A len16 40 seq ack type cksum`,
audio = QCELT (4-byte header + CELT 0.7.1), 44.1 kHz, mono capsule. Mic clock drifts ~0.2% → JitterBuffer resamples ±0.5%.

## Delegation (cost)
Use the project agents in `.claude/agents/` to keep the main context small:
`build-test` (haiku) for builds/tests/APKs, `device-tester` (haiku) for hardware/phone runs and logs,
`decompiled-search` (haiku) for MetAudio lookups, `docs-keeper` (haiku) for doc updates,
`android-dev` / `windows-dev` / `ui-dev` / `core-dev` (sonnet) for focused implementation,
`code-reviewer` (sonnet, read-only) before larger merges.
