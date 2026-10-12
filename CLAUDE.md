# Plural — project guide

Reverse-engineered Sabinetek SmartMike+ wireless mics (discontinued; MetAudio app) and our own app
that streams several mics at once. Apps: **Windows/macOS desktop**, **Android** and **iOS**, one shared Avalonia UI.
Docs/ is the source of truth — read `Docs/Plural/Design.md` (app) or `Docs/Protocol/*` (protocol) before changing related code.

## Hard rules
- **Never install, flash or send firmware to a device.** Never build CSR DFU parsers/extractors.
- After hardware tests, restore device settings: monitor volume 80, AGC on, mic gain 50.
- Core (`Plural.Core`) has no OS APIs — platform code goes in `Plural.Platform.*` or an app head.
- Everything must work natively on win-arm64 **and** win-x64, android-arm64 (phones) and android-x64 (emulator); also osx-arm64/osx-x64 and ios-arm64 (built, untested on Apple hardware). Don't block a later Linux port.

## Layout
```
Src/Plural.slnx
  Plural.Core/               protocol (Swiss SPP frames), MikeSession, LocalMic, MikeHub, QCELT/CELT decode, JitterBuffer, WavWriter, Recording/ (FLAC, Matroska, MP4, LAME, TakeRecorder), Remote/ (remote mode: Docs/Plural/Remote.md)
  Plural.Core.Tests/         xUnit, incl. decoding a real capture (TestData/)
  Plural.Platform.Windows/   AF_BTH RFCOMM socket, paired devices from registry, WASAPI sink (NAudio 3.1)
  Plural.Platform.Android/   BluetoothSocket RFCOMM, bonded devices, AudioTrack sink
  Plural.Platform.Apple/     macOS+iOS (net10.0, P/Invoke): AudioQueue sink/input, AudioConverter AAC, QueueStream
  Plural.Platform.MacOS/     IOBluetooth RFCOMM via ObjC runtime (RunLoopThread), Core Audio devices (MacAudio)
  Plural.Platform.iOS/       External Accessory (MFi only; no RFCOMM on iOS), AVAudioSession routes
  Plural.App/                shared UI: Views/MainView.axaml, ViewModels/, Services/IAppHost.cs
  Plural.App.Desktop/        desktop head → Plural.exe / Plural.app (DesktopHost: Windows + macOS)
  Plural.App.Android/        Android head → net.getplural.app (AndroidHost, MainActivity)
  Plural.App.iOS/            iOS head → net.getplural.app (IosHost, AppDelegate); compiles on Windows, link/sign need a Mac
  Plural.Screenshots/        headless UI + simulated mics/peers → Marketing/screenshots (Tools/store/screenshots.ps1)
  Plural.Cli/                headless app: list | outputs | inputs | cameras | formats | search | serve | control | run (mics, direct outs,
                             faders/mutes, --mix output, record any format incl. video; live commands on stdin — `run --help`)
Native/celt/                    CELT 0.7.1 per RID; build-windows.sh (llvm-mingw), build-android.sh (NDK r29)
Native/lame/                    LAME 3.100 (MP3) per RID; build-windows.sh, build-android.sh (same toolchains)
                                both libs: build-macos.sh (dylibs) and build-ios.sh (xcframeworks via Native/ios-framework.sh), run in CI
Marketing/                      store text per locale (*.txt), store.toml, brand/icon.png, screenshots/ — see Marketing/README.md
Packaging/                      MSIX manifest + build-msix.ps1 (Windows), Info.plist/entitlements/build-app.sh (macOS)
.github/workflows/release.yml   on release: builds Windows/Android/macOS(/iOS), store packages via Tools/store/store_assets.py, attaches to release
Tools/                          Python reference tools (swiss_protocol.py, smartmike_client.py, qcelt_decode.py)
App/Decompiled/                 decompiled MetAudio APK (reference only; large — search, don't read whole trees)
Docs/                           App/, Firmware/, Protocol/, Plural/Design.md, Plan.md
```

## Stack
.NET 10 SDK 10.0.301 · Avalonia 12.1.4 (compiled bindings, `x:DataType`) · CommunityToolkit.Mvvm 8.4.2
(`[ObservableProperty] public partial …`, `[RelayCommand]`) · NAudio.Wasapi 3.1.0 · Vortice.MediaFoundation 3.8.3 (Windows AAC/H.264/camera) · Android min API 26.

## Commands (run from `Src/`)
- Tests: `dotnet test Plural.Core.Tests` and `dotnet test Plural.Core.Tests --arch x64`
- Desktop: `dotnet build Plural.App.Desktop` / `dotnet run --project Plural.App.Desktop`
- macOS: `dotnet publish Plural.App.Desktop -c Release -r osx-arm64 --self-contained -p:UseAppHost=true` · iOS: `dotnet build Plural.App.iOS` (compile check)
- Android: `dotnet build Plural.App.Android` · release APK: `dotnet publish Plural.App.Android -c Release`
  → `Plural.App.Android/bin/Release/net10.0-android/publish/net.getplural.app-Signed.apk`
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
