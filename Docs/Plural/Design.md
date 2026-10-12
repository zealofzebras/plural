# Plural — application design

Goal: a simple app that connects to **multiple SmartMike+ mics at once**, exposes each as a **Windows input device**, and records them **simultaneously** with one Start/Stop. Runs on Windows and **Android**.
Apps can also record **together**: one controls others over Wi‑Fi or Bluetooth and collects their files into one take ([Remote.md](Remote.md)).
It also records **without any SmartMike**: local mics of the device (built-in, USB, wired or Bluetooth headset) can be added, each as its own stream next to (or instead of) SmartMikes.

## Name and identity
- Product: **Plural** — getplural.net. Android package `net.getplural.app`; Windows executable `Plural.exe`; projects `Plural.*`
  (`Src/Plural.slnx`); version and copyright in `Src/Directory.Build.props`. Formerly "SmartMike Hub" (`com.zealofzebras.smartmikehub`):
  desktop settings are copied once from `%AppData%\SmartMike Hub\`; on Android the new package is a separate install.
- "SmartMike" names the Sabinetek hardware and its protocol (`MikeSession`, `MikeHub`, `Docs/Protocol/`) and stays as is.
- UI: the header has one menu (Connect all, Scan, Add a microphone, Record with other devices, Recording settings, About Plural);
  the About sheet shows version, this device, the website and open-source credits.

## Constraints
- Native on **Windows ARM64 and x64**, and **Android** (arm64 phones, x64 emulator).
- **macOS** (desktop head, `osx-arm64`/`osx-x64`) and **iOS** (own head) are built but not yet tested on Apple hardware; **Linux** later — nothing may block it.
- Stack: **C# / .NET 10**, UI in **Avalonia** (Windows, macOS, Linux, Android, iOS).

## Architecture

```
┌──────── Plural.App (shared Avalonia UI) + heads: .Desktop, .Android, .iOS ───────┐
│ device list · per-mic status/battery/level · settings · output mapping · Record All │
└──────────────────────────────────────┬───────────────────────────────────────────────┘
                                       │ (plain .NET API, no UI types)
┌──────────────────────────── Plural.Core (net10.0, no OS APIs) ───────────────────┐
│ Protocol: frames, commands, TLVs, reports      MikeSession: handshake, clock sync,  │
│ QCELT header + CELT 0.7.1 (P/Invoke)           AUDIO_BEGIN/END, heartbeat, settings │
│ JitterBuffer (drift-compensated resampling)    MikeHub: N sessions, Record All       │
│ WavRecorder                                    Interfaces: IRfcommConnector,         │
│                                                IDeviceDiscovery, IAudioSinkFactory   │
└──────────────┬─────────────────────────────────────────────┬────────────────────────┘
               │                                             │
┌──────────────▼───────────── platform implementations ──────▼────────────────────────┐
│ Plural.Platform.Windows   RFCOMM: Winsock AF_BTH (connect by SPP UUID)            │
│ (net10.0-windows)            Discovery: paired devices (registry / BluetoothAPIs)    │
│                              Audio sink: WASAPI render → virtual cable endpoint      │
│ Plural.Platform.Android   RFCOMM: BluetoothSocket (SPP UUID) · bonded devices     │
│ (net10.0-android)            Audio sink: AudioTrack (float, low latency) → monitor   │
│ later: Platform.Linux        RFCOMM: AF_BLUETOOTH socket · sink: PipeWire source     │
│ Plural.Platform.MacOS     RFCOMM: IOBluetooth (ObjC runtime) · Core Audio devices  │
│ (net10.0)                    sink/input: AudioQueue · AAC: AudioConverter         │
│ Plural.Platform.iOS       External Accessory (MFi only) · AVAudioSession routes   │
│ (net10.0-ios)                AudioQueue + AAC shared via Plural.Platform.Apple       │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

- **Mic sources**: every channel wraps an `IMicSource` (mono PCM, state, peak, `PcmReceived`). `MikeSession` is the SmartMike source; `LocalMic` wraps an OS input through `IAudioInputPlatform` (`WasapiAudio` on Windows, `AndroidAudioInput` on Android). Platforms deliver 44.1 kHz mono 16-bit; recording and live output treat both kinds the same. Local mic IDs are `input:<platform id>` and are saved in the app settings (`LocalMics`) so added mics come back on start.
- **Core has no OS dependencies** — runs on any .NET 10 RID and is unit-tested against real captures.
- **Native CELT**: `Native/celt/runtimes/<rid>/native/celt071.{dll,so,dylib}`, built from the stock CELT 0.7.1 source: `win-x64`, `win-arm64` (llvm-mingw), `android-arm64`, `android-x64` (NDK r29), `osx-*` (CI). Always compiled with `-DVAR_ARRAYS`: without it CELT uses one process-wide scratch stack and two mics decoding on their own threads corrupt memory (test `Capture_DecodesIdenticallyOnSeveralThreadsAtOnce`). Loaded with `[LibraryImport("celt071")]`; on Android the APK carries it as `lib/<abi>/libcelt071.so`.
- **Native LAME** (MP3 only): `Native/lame/runtimes/<rid>/native/mp3lame.dll` or `libmp3lame.so`, LAME 3.100, same four RIDs. Resolved with the CELT lib in `Plural.Core/Codec/NativeLibs.cs`.
- **Recording formats and video**: `Plural.Core/Recording/` (managed FLAC, Matroska, MP4/M4A writers, clock-locked tracks, `TakeRecorder`); platform AAC and H.264 come from `IAacEncoderFactory` and `IVideoPlatform`. Full spec and status: [Recording.md](Recording.md).
- **App heads** supply an `IAppHost` (hub factory, recording folder, permission prompt, open/share recordings, keep-awake). The UI lives in `MainView` (a `UserControl`) so desktop wraps it in a window and Android shows it directly.
- **Plural.Cli** (`net10.0`): headless runner for testing and future Linux/macOS service use.

## Virtual microphones (Windows)
Pluggable `IAudioSink`:
1. **v1** — render decoded PCM to an existing virtual cable's playback side (e.g. VB-CABLE "CABLE Input"; ARM64 supported since Oct 2024). The user picks one cable per mic in the UI. Apps record from the cable's capture side.
2. **later** — own ACX virtual-audio driver exposing one capture endpoint per SmartMike (named after the mic). Needs Microsoft attestation signing.

macOS: AudioServerPlugIn (BlackHole-style). Linux: PipeWire virtual source per mic (no driver).

## Android
- Lists **paired** SmartMikes (pair in Android Bluetooth settings first); no scanning, so only `BLUETOOTH_CONNECT` is requested (Android 12+).
- **Local mics** (`AndroidAudioInput`, AudioRecord 44.1 kHz mono with `SetPreferredDevice`): phone mic(s), wired headset, USB, Bluetooth headset (SCO; LE audio on 12+). Bluetooth headset mics need the voice link: `SetCommunicationDevice` (12+) or `StartBluetoothSco` (older), released when the last one closes. Needs `RECORD_AUDIO` (asked on first connect) and `MODIFY_AUDIO_SETTINGS`. Several local mics can be added; Android often captures only one input device at a time, so ~1 s after start (4 s for Bluetooth) each input checks `RoutedDevice` and `IsClientSilenced` (API 29+) and fails with a clear message instead of recording the wrong mic. **Not yet tested on a phone.**
- Each mic's output picker routes live audio to the phone speaker, wired/USB headphones or a Bluetooth output (`AudioTrack.SetPreferredDevice`). There is no "virtual input device" on Android.
- Recordings go to app storage (`Android/data/net.getplural.app/files/Recordings/<yyyyMMdd_HHmmss>/`, reachable over USB). **Share** sends the last take as one file, or `<take>.zip` for multi-file takes, through the share sheet (FileProvider).
- Video: Camera2 (`AndroidVideoPlatform`/`AndroidVideoCapture`) with an ImageReader preview (≤640 px, 15 fps) and a MediaCodec `video/avc` surface encoder. Needs the `CAMERA` permission (requested by `AndroidHost` before opening the camera). Camera path not yet tested on a phone.
- AAC via MediaCodec `audio/mp4a-latm` (`AndroidAacEncoder`).
- The screen stays on while recording. Recording with the screen off needs a foreground service (later).
- min Android 8.0 (API 26); 16 KB-page-aligned native lib for Android 15+.

## Mixer mode
- A layout next to the cards (bottom-bar **Mixer** chip, switchable at any time, also mid-take), independent of Audio/Video: the
  Audio/Video button stays, and in video mode the camera shows as a small window (floating top-right on wide windows, docked above
  the console on phones) with REC badge and camera switch; the video take records as usual.
  One console-style channel strip per mic: power (connect), state/battery, SmartMike gain knob + AGC/NS buttons + phones knob
  (device settings, as on the cards), direct output picker, fader with dB scale, post-fader LED meter, MUTE, name strip.
- **Channel fader and mute** live in `MikeChannel` (`FaderDb` −60 = −∞ … +12 dB, `Muted`): applied once in the channel, so the
  recording (`TakeRecorder` subscribes to `MikeChannel.PcmProcessed`, not the raw source), the direct output and the mix all get it,
  from the next audio block, also mid-take. 0 dB is bit-exact pass-through.
- **Master strip / mixed live output**: `MixBus` (`MikeHub.Mix`) sums every channel after fader and mute into one mono output at
  44.1 kHz, each channel through its own `JitterBuffer` (drift-compensated), with master fader, mute and meter. Live only: each mic still
  records to its own track. Platform sinks take an `IAudioSource` (`JitterBuffer` or `MixBus`).
- Controls: `Controls/Knob.cs`, `Fader.cs` (console taper, relative drag so touching never jumps the level, double-tap = 0 dB),
  `MixerMeter.cs`. Fader/mute per mic (by source id), mix output, master fader and mixer mode persist in settings.
- Preview without hardware: `dotnet run --project Src/Plural.Screenshots -c Release -- --profile desktop --out <dir> --mixer`.

## macOS
- Desktop head (`Plural.App.Desktop`, same `Plural` executable) published for `osx-arm64`/`osx-x64` and wrapped into `Plural.app` by
  `Packaging/macos/build-app.sh`; `DesktopHost` picks `Plural.Platform.MacOS` when `OperatingSystem.IsMacOS()`.
- `Plural.Platform.MacOS` is plain `net10.0` with P/Invoke only (no Xamarin bindings, so the Avalonia desktop head stays one project):
  - `MacBluetooth`: IOBluetooth through the Objective-C runtime (`ObjC.cs`). Paired devices from `[IOBluetoothDevice pairedDevices]`;
    connect = cached SDP record for the SPP UUID (or `performSDPQuery:`), then `openRFCOMMChannelAsync:` with a runtime-created delegate
    class. All IOBluetooth calls and callbacks run on one CFRunLoop thread (`RunLoopThread`), so it also works in the CLI. Writes use
    `writeSync:` in MTU-sized chunks. No RFCOMM server yet, so remote mode on macOS is Wi-Fi only.
  - `MacAudio`: Core Audio HAL device list (IDs are device UIDs; hidden devices skipped; BlackHole/Loopback count as virtual devices
    and sort first), output and capture through AudioQueue (`kAudioQueueProperty_CurrentDevice`).
- Shared with iOS in `Plural.Platform.Apple` (`net10.0`): `AudioQueueSink` (float, 3 × 512 frames), `AudioQueueInput` (44.1 kHz mono
  16-bit; the queue stopping on its own is reported as an error), `AppleAacEncoder` (AudioConverter AAC-LC), `QueueStream`
  (callback transport → `Stream`).
- Permissions: Bluetooth and microphone prompts come from macOS on first use (`NSBluetoothAlwaysUsageDescription`,
  `NSMicrophoneUsageDescription` in `Packaging/macos/Info.plist`); an unbundled `dotnet run` attributes them to the terminal.
- Recordings: `~/Documents/Plural Recordings`; settings: `~/Library/Application Support/Plural/settings.json`.
- Native libs: `Native/celt/build-macos.sh`, `Native/lame/build-macos.sh` (built in the release workflow).
- Not yet: camera (AVFoundation + VideoToolbox), own virtual device (AudioServerPlugIn; use BlackHole meanwhile).

## iOS
- Head `Plural.App.iOS` (`net10.0-ios`, iOS 15+, iPhone and iPad, `AvaloniaAppDelegate<App>`), `IosHost`, platform `Plural.Platform.iOS`.
- **Bluetooth: iOS has no public RFCOMM/SPP API.** A Classic Bluetooth accessory is reachable only through External Accessory, which
  requires an MFi (iAP2) accessory, its protocol string under `UISupportedExternalAccessoryProtocols` in `Info.plist`, and — for the
  App Store — the accessory maker's approval with Apple. Whether the SmartMike+ is MFi is unknown. `IosAccessories` lists connected
  EA accessories as paired devices (address = stable hash of the serial number with the locally administered bit set, since iOS hides
  MACs) and opens an `EASession` on the main run loop; if the mic's protocol is not declared, the error names the protocols it offers.
- Local mics: the audio session's available inputs (built-in, wired/USB, Bluetooth HFP); one records at a time (`SetPreferredInput`).
  Monitoring goes to the current route. Session: PlayAndRecord, default to speaker, A2DP/AirPlay allowed.
- Recordings in `Documents/Recordings` (visible in the Files app); Share via `UIActivityViewController`. Background mode `audio` keeps
  recording with the screen locked; the screen stays on while recording.
- Remote mode: Wi-Fi only; subnet broadcast discovery needs Apple's `com.apple.developer.networking.multicast` entitlement.
- Native libs: dynamic frameworks in `Native/{celt,lame}/runtimes/ios/*.xcframework` from `build-ios.sh` (via `Native/ios-framework.sh`);
  `NativeLibs` loads `Frameworks/<name>.framework/<name>`.
- Not yet: camera; SmartMike connection (pending the MFi question).

## Recording
`MikeHub.StartRecordingAll(root)` / `StopRecordingAllAsync()` start and stop every connected mic through `TakeRecorder`, at the same instant. Take folder: `<root>/<yyyyMMdd_HHmmss>/`. Formats: WAV, M4A, MP3, MKA, MKV, MP4 (see [Recording.md](Recording.md)); the default is WAV, 44.1 kHz mono 16-bit (the capsule is mono; L = R). Each mic is locked to `MediaClock`, so takes stay aligned over long recordings. Recording works with or without a virtual cable.

## Local mics (Windows)
`WasapiAudio` also implements `IAudioInputPlatform`: every active capture endpoint, shared mode, and the engine converts the input to 44.1 kHz mono 16-bit (AUTOCONVERTPCM). Any number can be captured at once. If the privacy setting "Let desktop apps access your microphone" is off, opening fails with a message that points to it. CLI: `inputs`, `run --input NAME [--input NAME…]`.

## Multiple mics
Each SmartMike has its own RFCOMM link and `MikeSession`. One stream ≈ 135 kbit/s; a BR/EDR adapter should carry several. Exact limit to be measured.

## Project layout
```
Src/
  Plural.slnx
  Plural.Core/               protocol, session, codec, jitter buffer, Recording/ (formats, TakeRecorder)
  Plural.Core.Tests/         xUnit — frames, headers, decode of Captures/*
  Plural.Platform.Windows/   RFCOMM, discovery, WASAPI sink
  Plural.Platform.Android/   RFCOMM (BluetoothSocket), bonded devices, AudioTrack sink
  Plural.Platform.Apple/     macOS + iOS: AudioQueue output/input, AudioConverter AAC, QueueStream
  Plural.Platform.MacOS/     IOBluetooth RFCOMM (ObjC runtime), Core Audio devices
  Plural.Platform.iOS/       External Accessory, AVAudioSession inputs/outputs
  Plural.Cli/                headless runner
  Plural.App/                shared Avalonia UI (MainView, view models, IAppHost, settings JSON)
  Plural.App.Desktop/        desktop head → Plural.exe
  Plural.App.Android/        Android head → net.getplural.app.apk
  Plural.App.iOS/            iOS head → net.getplural.app (.ipa)
Native/celt/                    per-RID CELT binaries (build-windows.sh, build-android.sh, build-macos.sh, build-ios.sh)
Native/lame/                    per-RID LAME 3.100 binaries (same four scripts)
```

## Status (2026-10-11)
| Part | State |
|---|---|
| `Plural.Core` | Done: protocol, session (connect/handshake/sync/audio/settings/reconnect), QCELT + CELT P/Invoke, drift-compensated jitter buffer, WAV recorder, hub with Record All. Recording formats built (FLAC, Matroska, MP4/M4A, LAME MP3, clock-locked tracks, `TakeRecorder`, take zip export); **not yet tested with real mics + camera end-to-end** |
| `Plural.Core.Tests` | 46 tests pass on **win-arm64 and win-x64**: CELT decode of a real capture (±2 LSB vs Python), FLAC round-trip, Matroska/MP4 structure, rotation matrix, LAME, sync drift/dropout, real capture → WAV/MKA bit-exact, fake end-to-end of all six formats |
| `Plural.Platform.Windows` | Done: RFCOMM via `Socket(AF_BTH)` + SPP UUID, paired-device list from registry, WASAPI output (NAudio 3.1 `WasapiPlayer`). Media Foundation (Vortice.MediaFoundation 3.8.3): AAC encoder (96/128/160/192 kbps), camera capture (NV12, preview 640x360@15), H.264 hardware MFT with software fallback (`PLURAL_SOFTWARE_H264=1` forces software). Verified on Surface arm64 + x64: 1080p30, 3 keyframes/3 s, ~10.5 Mbps. Intel/AMD/NVIDIA hardware encoders untested |
| Local mics | `IMicSource`/`LocalMic` in Core with unit tests (fake platform: lifecycle, errors, WAV take at 44.1 and 48 kHz). Windows WASAPI capture verified on Surface arm64 (CLI `run --input Microphone --record 3`: 3.02 s, 44.1 kHz mono). UI: "Add mic" sheet, local cards (output picker only; no SmartMike settings). Android AudioRecord path builds; **not yet run on a phone** (multiple inputs, Bluetooth headset) |
| Remote mode | Protocol, Wi‑Fi + Bluetooth transports, controller/remote, take manifest and zip, UI (Devices sheet, remote cards, approval, banner) built; 6 tests. CLI serve ↔ control verified over Wi‑Fi on one PC. **Two-device, Bluetooth, Android and UI not yet tested** — see [Remote.md](Remote.md) |
| `Plural.Cli` | `search`, `serve`, `control`, `list`, `outputs`, `inputs`, `run [--input X] [--output X] [--record N] [--format F]` — verified on hardware: connect, stream, record (440 frames, 0 errors), live output (0 errors). `--format` (WAV default) not yet tested on hardware |
| CLI mixer/record (2026-10-12) | `run` has the app's capabilities: SmartMikes by MAC or `--mic NAME`, local mics `--input`, `--set`, direct outputs `--output`, `--fader MIC=DB`, `--mute MIC`, `--mix OUTPUT`, `--master DB`, `--master-mute`, recording `--record N --format F --folder --matroska --aac --mp3`, video `--camera --resolution --fps --video-mbps`; commands on stdin (keyboard or pipe) while it runs: status, record [N], stop, fader, mute/unmute, out, mix, master, set, connect/disconnect, format, wait, quit. Windows hub now has AAC + camera. Verified on Surface arm64 with the built-in mic: fader/mute changes land in the WAV, mix to speakers, WAV/M4A/MKV/MP4 takes. SmartMike run not retested (mic was off) |
| CLI MCP server (2026-10-12) | `plural mcp [run options]`: the `run` mixer as an MCP server (stdio, newline-delimited JSON-RPC, hand-rolled on System.Text.Json; stdout = protocol only, logs go to stderr). Starts empty if no mics match. Tools: devices, status, add_mic, connect, disconnect, record [seconds], stop, fader, mute, set_output, set_mix, master, set_setting, set_format; each result = lines the action printed + events since the previous call (state changes, saved takes). Console `run` also gained `devices` and `add MAC\|NAME`. Verified on Surface arm64 with the built-in mic (scripted client: add, fader, mute, timed and manual takes). SmartMike via MCP not yet tested |
| `Plural.App` | UI redesign built: design system (dark/light, coral Record, blue accent), Audio/Video mode with camera preview, mic cards with status pill, battery, gradient level meter with peak hold, round Record button with format chip, settings sheet (format, Matroska codec, AAC/MP3 bitrate, video res/fps/bitrate, folder), persisted JSON settings, recent takes, save toast. Not yet hardware-tested |
| `Plural.App.Desktop` | Runs on Windows with the shared UI (hosted as `MainView`); `OpenFolder` via `IAppHost`. Redesigned UI not yet tested on the desktop head |
| `Plural.App.Android` | Builds; Debug and Release APKs (arm64 + x64) with CELT and LAME included. Installed on a Pixel 8 (Android 17) and launches; the previous version recorded WAV on the phone. The new formats, AAC and the camera are **not yet run on the phone** |
| `Plural.Platform.MacOS` / `.Apple` | Built 2026-10-11: IOBluetooth RFCOMM, Core Audio output/input, AudioQueue, AAC; the desktop head and CLI use it on macOS; cross-publish for `osx-arm64` works. **Never run on a Mac**: Bluetooth connect, audio and AAC untested. No camera |
| `Plural.App.iOS` / `Plural.Platform.iOS` | Built 2026-10-11; compiles on Windows, while linking, signing and running need a Mac. **Never run on a device.** SmartMike connection depends on MFi support (unknown); local mics, recording, share and Wi-Fi remote should work. No camera |
| Virtual cable | Not installed yet; the output picker lists cables first when present |

### Known open items (recording and video)
- Audio runs ~Bluetooth latency behind video: measure with a clap test, then compensate.
- MKV rotation sign (`ProjectionPoseRoll`) to verify in players; MP4 rotation is the `tkhd` matrix.
- MP4/M4A need a seekable stream and write `moov` at the end: a crash loses those takes (MKA/MKV survive).
- No foreground service: the Android screen must stay on (the app keeps it on while recording).
- Camera privacy-denied path untested.

### Build notes
- Native CELT: `bash Native/celt/build-windows.sh` (llvm-mingw 20240619). Zig 0.13's aarch64-windows DLLs fail to load ("incorrect format"); don't use them.
- Native LAME 3.100: `bash Native/lame/build-windows.sh` (win-x64, win-arm64) and `bash Native/lame/build-android.sh` (android-arm64, android-x64, NDK r29, same as CELT). Outputs go to `Native/lame/runtimes/<rid>/native/`.
- `dotnet test Src/Plural.Core.Tests` (ARM64) and `dotnet test Src/Plural.Core.Tests --arch x64` both pass.
- Android CELT: `bash Native/celt/build-android.sh` (NDK r29 in `%LOCALAPPDATA%\Android\Sdk\ndk`, installed with `sdkmanager --sdk_root=%LOCALAPPDATA%\Android\Sdk "ndk;29.0.14206865"`; `sdkmanager.bat` needs a `JAVA_HOME` without parentheses, e.g. the Microsoft JDK).
- Desktop: `dotnet run --project Src/Plural.App.Desktop`. Windows-only code is marked `[SupportedOSPlatform("windows")]` and chosen in `Plural.App.Desktop/DesktopHost.cs`.
- macOS: `dotnet publish Src/Plural.App.Desktop -c Release -r osx-arm64 --self-contained -p:UseAppHost=true -o out/publish/osx-arm64`
  (works from Windows), then `bash Packaging/macos/build-app.sh …` on a Mac (see the release workflow). macOS-only code is marked
  `[SupportedOSPlatform("macos")]`.
- iOS: `dotnet build Src/Plural.App.iOS` compiles on Windows; device/simulator builds, signing and `.ipa` need a Mac with Xcode
  (`dotnet publish Src/Plural.App.iOS -c Release -r ios-arm64` with a signing identity and provisioning profile).
- Android: `dotnet publish Src/Plural.App.Android -c Release` → `bin/Release/net10.0-android/publish/net.getplural.app-Signed.apk` (debug-key signed, for sideloading). With a phone on USB debugging: `dotnet build Src/Plural.App.Android -t:Install` or `adb install -r <apk>`.
