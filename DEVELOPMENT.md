# Plural — developer guide

How Plural is built, how to build it yourself, and how to contribute. For what the app does, see the [README](README.md).

## Platform status

| Platform | State |
|---|---|
| Windows 10/11, arm64 and x64 | Main development platform; tested with SmartMike+ mics |
| Android 8.0+ (arm64 phones, x64 emulator) | Builds and runs on a Pixel 8; newer features still being tested on phones |
| macOS (arm64, x64) | Builds; not yet run on a Mac |
| iOS | Compiles; not yet run on a device. SmartMike support depends on Apple's MFi rules (iOS has no plain Bluetooth serial) |
| Linux | Planned; nothing in the design blocks it |

The detailed status of each part is in [Docs/Plural/Design.md](Docs/Plural/Design.md#status-2026-10-11).

## How it works

```
 SmartMike+ ──Bluetooth SPP (RFCOMM)──►  MikeSession ─┐
 (QCELT audio, control)                  (handshake,  │
                                          settings)   ├─► MikeChannel ─► fader/mute ─┬─► recorder (WAV/FLAC/AAC/MP3/MKV/MP4)
 local mic ──OS audio API──────────────► LocalMic ────┘   (jitter buffer,            ├─► direct output per mic
                                                           drift resampling)         └─► mix bus ─► mix output
```

- **The SmartMike protocol** was reverse engineered from the original MetAudio app and live captures. Each mic exposes a
  private serial (SPP) channel. Plural sends small framed commands over it, and the mic streams audio as
  **QCELT**: a 4-byte header plus a CELT 0.7.1 frame, 44.1 kHz mono. The full write-up is in [SmartMike/](SmartMike/README.md) and [Docs/Protocol/](Docs/Protocol/).
- **Clock drift**: each mic's clock drifts by about 0.2 %, so the jitter buffer resamples every mic (±0.5 %) onto one clock.
- **`Plural.Core`** holds everything that isn't OS-specific: protocol frames, sessions, CELT decoding (native `celt071` via P/Invoke),
  a drift-compensating jitter buffer, the mixer, and the recording containers (WAV, FLAC, Matroska, MP4 written in managed code).
- **Platform projects** add Bluetooth sockets, audio in/out, AAC and H.264 encoders, and cameras for each OS:
  Windows (Winsock `AF_BTH`, WASAPI, Media Foundation), Android (BluetoothSocket, AudioTrack/AudioRecord, MediaCodec,
  Camera2), macOS (IOBluetooth, Core Audio) and iOS (External Accessory, AVAudioSession).
- **`Plural.App`** is one shared [Avalonia](https://avaloniaui.net) UI. Small app heads wrap it for the desktop, Android and iOS.
- **`Plural.Cli`** (`plural`) does everything the app does without a UI: mics, direct outputs, faders/mutes, a mix output,
  recording in any format, remote control, and an MCP server (see below).

The architecture, platform notes and design decisions are in [Docs/Plural/Design.md](Docs/Plural/Design.md); the recording
pipeline is in [Docs/Plural/Recording.md](Docs/Plural/Recording.md); recording across devices is in
[Docs/Plural/Remote.md](Docs/Plural/Remote.md).

## Repository layout

| Folder | Contents |
|---|---|
| [`Src/`](Src/) | The app: `Plural.slnx` with Core, Core.Tests, Platform.\*, App (shared UI), App.Desktop / .Android / .iOS heads, Cli, Screenshots |
| [`Native/`](Native/) | Build scripts and per-platform binaries for the two native libraries: CELT 0.7.1 (audio decoding) and LAME 3.100 (MP3) |
| [`Docs/`](Docs/README.md) | Design docs for the app ([Docs/Plural/](Docs/Plural/)) and the SmartMike findings (protocol, app, firmware) |
| [`SmartMike/`](SmartMike/README.md) | The reverse-engineering material: the original app, firmware images, decompiled code |
| [`Tools/`](Tools/) | Python reference tools for the SmartMike protocol, and the store-listing builder (`Tools/store/`) |
| [`Marketing/`](Marketing/README.md) | Store text, icon and screenshots |
| [`Packaging/`](Packaging/) | Windows MSIX and macOS app bundle packaging |
| [`.github/workflows/`](.github/workflows/release.yml) | Release pipeline: builds every platform and the store packages when a GitHub release is published |

## Building

### Prerequisites

- **.NET SDK 10.0.301** ([download](https://dotnet.microsoft.com/download/dotnet/10.0))
- **Android**: `dotnet workload install android`, JDK 21, and the Android SDK. To install the SDK packages Plural needs, run
  `dotnet build Src/Plural.App.Android -t:InstallAndroidDependencies -f net10.0-android`.
- **iOS**: `dotnet workload install ios`. It compiles on Windows; linking, signing and running need a Mac with Xcode.
- **Native CELT library**: `celt071` is **not committed**, so build it once after cloning (see below). LAME binaries for
  Windows and Android are committed.

### CELT (once after cloning)

Download [celt-0.7.1.tar.gz](https://downloads.xiph.org/releases/celt/celt-0.7.1.tar.gz) and extract it to
`Tools/ext/src/celt-0.7.1`, then run the script for your target in bash (Git Bash on Windows):

```bash
# Windows x64 + arm64 DLLs. Needs llvm-mingw 20240619 (github.com/mstorsjo/llvm-mingw);
# set LLVM_MINGW to it unless it is at Tools/ext/llvm-mingw-20240619-ucrt-aarch64.
bash Native/celt/build-windows.sh

# Android arm64 + x64. Needs NDK r29 (sdkmanager "ndk;29.0.14206865"); set ANDROID_NDK if it isn't in the default SDK folder.
bash Native/celt/build-android.sh

# macOS dylibs / iOS xcframeworks (on a Mac)
bash Native/celt/build-macos.sh
bash Native/celt/build-ios.sh
```

The libraries land in `Native/celt/runtimes/<rid>/native/`. The release workflow builds them the same way.

### Build and run (from `Src/`)

```bash
dotnet test Plural.Core.Tests                     # unit tests, including decoding a real SmartMike capture
dotnet test Plural.Core.Tests --arch x64          # also on x64 when you are on an arm64 machine

dotnet run --project Plural.App.Desktop           # desktop app (Windows, macOS)
dotnet build Plural.App.Android                   # Android debug build
dotnet publish Plural.App.Android -c Release      # → Plural.App.Android/bin/Release/net10.0-android/publish/net.getplural.app-Signed.apk
dotnet publish Plural.App.Desktop -c Release -r osx-arm64 --self-contained -p:UseAppHost=true   # macOS
dotnet build Plural.App.iOS                       # iOS compile check

dotnet run --project Plural.Cli -- help           # command line: list, inputs, outputs, run, serve, control, mcp, …
```

Install an APK on a phone with `adb install -r <apk>`. SmartMikes must be **paired in the OS Bluetooth settings** first;
Plural lists paired devices and doesn't scan for new ones.

### Using Plural from an AI assistant

`plural mcp` runs the mixer as an MCP server on stdin/stdout, with tools to list devices, add mics, set faders, and record and stop.
For example, in Claude Code:

```bash
claude mcp add plural -- <path-to>/Plural.Cli.exe mcp
```

## Releases

Releases are built by publishing a GitHub release; [release.yml](.github/workflows/release.yml) builds Windows (MSIX),
Android (APK/AAB), macOS and iOS packages and attaches them with the store listings.

## Contributing

Issues and pull requests are welcome, especially testing on hardware we don't have: Macs, iPhones, other Android phones,
and Linux for the planned port.

- **Read the docs first.** `Docs/` is the source of truth: [Docs/Plural/Design.md](Docs/Plural/Design.md) for the app,
  [Docs/Protocol/](Docs/Protocol/) for the SmartMike protocol. When you change behaviour, update the doc and the
  status table in the same pull request.
- **Keep `Plural.Core` free of OS APIs.** Platform code goes in `Plural.Platform.*` or an app head, behind the interfaces in
  `Plural.Core/Platform/`.
- **Every platform counts.** Changes must build and work on win-arm64 and win-x64, android-arm64 and android-x64, and still
  build for osx-arm64/osx-x64 and ios-arm64. Don't add anything that would block a Linux port.
- **Run the tests** on both arm64 and x64 (`dotnet test` with and without `--arch x64`) before opening a pull request.
- **Stack conventions**: Avalonia 12 with compiled bindings (`x:DataType`), CommunityToolkit.Mvvm
  (`[ObservableProperty] public partial …`, `[RelayCommand]`). Match the style of the code around your change.
- **Hardware safety**:
  - Never install, flash or send firmware to a mic, and don't add firmware-update code.
  - After testing on a mic, put its settings back to the defaults: monitor volume 80, AGC on, mic gain 50.
- **Protocol findings**: document them in `Docs/Protocol/` and mark each claim **Verified**, **Inferred** or **Open**,
  with evidence. See [SmartMike/README.md](SmartMike/README.md) for how the reverse engineering is organised.
- **Store text**: product names, subtitles and keywords must not contain "SmartMike" or "Sabinetek"
  (see [Marketing/README.md](Marketing/README.md)).
