# Sabinetek SmartMike+ reverse engineering

Goal: keep obsolete Sabinetek SmartMike+ microphones usable without the discontinued MetAudio / SMike+ apps — understand the Bluetooth protocol, stream mic audio (and monitor audio back) from a computer, and investigate the firmware.

## Folder layout

| Folder | Contents |
|---|---|
| `App/Apk/` | Original MetAudio 1.5.2 APK (APKPure download) |
| `App/Decompiled/unzipped/` | Raw unzip of the APK (dex, native `.so` libs, assets) |
| `App/Decompiled/jadx/` | jadx 1.5.1 decompile — `sources/` (Java) and `resources/` (decoded manifest, res) |
| `App/Decompiled/.jadx-tool/` | The jadx binary used, so the decompile is reproducible |
| `App/Reference/SwissSample-android/` | Sabinetek's public Swiss SDK sample app (github.com/ztimc/SwissSample-android, 2018) |
| `Firmware/Updater/` | Official Windows firmware updater installer + its extracted contents |
| `Firmware/Images/` | Firmware images (`.dfu`) extracted or collected |
| `Tools/` | Our own scripts: `swiss_protocol.py` (frame encoder/decoder), `smartmike_client.py` (live test client), `qcelt_decode.py` (audio → WAV), `smartmike_bridge.py` (live mic → any audio output), `ghidra_scripts/` |
| `Tools/bin/` | Built binaries (`celt071_x64.dll` = CELT 0.7.1 decoder) |
| `Tools/ext/` | Third-party tools and sources (Ghidra, Zig, CELT 0.7.1 source, Ghidra project) — not ours, can be re-downloaded |
| `Captures/` | Live captures from `smartmike_client.py` (raw rx/tx bytes, logs, audio frames, decoded WAV) |
| `App/Decompiled/native/` | Ghidra C output for native libraries |
| `Src/` | **SmartMike Hub** app (C# / .NET 10 / Avalonia): `SmartMike.slnx` — Core, Core.Tests, Platform.Windows, Cli, App. See [Docs/MikeHub/Design.md](Docs/MikeHub/Design.md) |
| `Native/celt/` | CELT 0.7.1 native builds per RID + `build-windows.sh` (llvm-mingw) |
| `Docs/` | All findings — start at [Docs/README.md](Docs/README.md) |
