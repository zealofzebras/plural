# Sabinetek SmartMike+ reverse engineering

Goal: keep the obsolete Sabinetek SmartMike+ wireless microphones usable without the discontinued MetAudio / SMike+ apps.
That means understanding the Bluetooth protocol, streaming mic audio (and monitor audio back) from a computer or phone, and
documenting the firmware.

This folder holds the source material: the original app, firmware images and decompiled code. The findings are written up in
[`../Docs/`](../Docs/README.md), and working reference tools are in [`../Tools/`](../Tools/).

## Findings at a glance

| Topic | Summary | Details |
|---|---|---|
| Hardware | CSR8670 Bluetooth SoC running a customized CSR ADK "sink" (headset) firmware; internal model `Sabine-S610`. MAC prefixes `28:36:38` and `12:FC:08` | [Firmware/README.md](../Docs/Firmware/README.md) |
| Bluetooth profiles | A standard headset (HFP/HSP narrowband mic, A2DP to the earpiece, AVRCP), plus a **private SPP channel** that carries control and full-band mic audio | [Protocol/README.md](../Docs/Protocol/README.md) |
| Framing | `FF 5A` sync, 16-bit length, `40`, seq/ack, type, checksum; then an `SS` sub-header with a 16-bit command and TLV payload | [Protocol/SPP-Framing.md](../Docs/Protocol/SPP-Framing.md) |
| Commands | About 30 commands: version, status/battery, buttons, mic gain, monitor, AGC, noise suppression, effects, clock sync, audio start/stop, OTA. Setting writes take effect immediately, persist on the device, and are not acknowledged | [Protocol/Commands.md](../Docs/Protocol/Commands.md) |
| Audio | `AUDIO_STREAM_VER` packets: timestamp + **QCELT** frames (4-byte header + stock CELT 0.7.1 bitstream), 44.1 kHz, mono capsule, ~130 kbit/s at "high". The mic's clock drifts ~0.2 % against the host | [Protocol/Audio.md](../Docs/Protocol/Audio.md) |
| Authentication | The app has a handshake library, but the device does not enforce it | [Protocol/Authentication.md](../Docs/Protocol/Authentication.md) |
| Original app | MetAudio 1.5.2 (`com.sabinetek.app`), built on Sabine's "Swiss" SDK; obfuscated class map; backend endpoints incl. firmware update check | [App/README.md](../Docs/App/README.md), [Class-Map.md](../Docs/App/Class-Map.md), [Server-API.md](../Docs/App/Server-API.md) |
| Firmware | CSR `CSR-dfu2` images with a USB DFU suffix; PC USB updater vs. OTA through the app | [DFU-Format.md](../Docs/Firmware/DFU-Format.md), [Update-Methods.md](../Docs/Firmware/Update-Methods.md) |

Tested against SmartMike+ firmware 0.4.37, hardware 0.1.1. Open questions and the work plan are in
[Docs/Plan.md](../Docs/Plan.md); external references are in [Docs/Sources.md](../Docs/Sources.md).

Every doc marks its claims as **Verified** (seen in code, binaries or captures, with the file or offset), **Inferred** (with
the evidence), or **Open**.

## Folder layout

In this folder:

| Folder | Contents | In git |
|---|---|---|
| `App/Apk/` | Original MetAudio 1.5.2 APK (APKPure download) | yes |
| `App/Reference/SwissSample-android/` | Sabinetek's public Swiss SDK sample app ([github.com/ztimc/SwissSample-android](https://github.com/ztimc/SwissSample-android), 2018) | yes |
| `App/Decompiled/unzipped/` | Raw unzip of the APK (dex, native `.so` libs, assets) | no |
| `App/Decompiled/jadx/` | jadx 1.5.1 decompile: `sources/` (Java) and `resources/` (decoded manifest, res) | no |
| `App/Decompiled/.jadx-tool/` | The jadx binary used, so the decompile is reproducible | no |
| `App/Decompiled/native/` | Ghidra C output for the native libraries (e.g. `libjni-smartdecoder.so.c`) | no |
| `Firmware/Updater/` | Official firmware updaters: Windows (`SmartMikeDFU20200702_1_1.exe`) and macOS (`SmartMike_mac.dmg.zip`); `extracted*/` holds their unpacked contents | installers only |
| `Firmware/Images/` | Firmware images (`.dfu`): SmartMike+ 0.4.32_0702 and two SwissSample OTA images | yes |

Elsewhere in the repository:

| Folder | Contents |
|---|---|
| [`../Docs/`](../Docs/README.md) | All findings: [Protocol/](../Docs/Protocol/), [App/](../Docs/App/), [Firmware/](../Docs/Firmware/) |
| [`../Tools/`](../Tools/) | Our reference scripts (below) and `ghidra_scripts/` (`ExportDecompiled.java`, headless C export) |
| `../Tools/bin/` | Built binaries: `celt071_x64.dll` (CELT 0.7.1 decoder used by the Python tools). Not in git |
| `../Tools/ext/` | Third-party tools and sources: Ghidra 12.1.4, Zig, llvm-mingw, CELT 0.7.1 source, the Ghidra project. Not in git; re-downloadable |
| `../Captures/` | Output of `smartmike_client.py`: raw rx/tx bytes, logs, audio frames, decoded WAV per run. Not in git |

### Recreating the parts that aren't in git

- **Decompile**: unzip the APK into `App/Decompiled/unzipped/`, and run jadx 1.5.1 on it with output to `App/Decompiled/jadx/`.
- **Native libraries**: import `App/Decompiled/unzipped/lib/<abi>/*.so` into Ghidra and export C with
  `../Tools/ghidra_scripts/ExportDecompiled.java` (headless) into `App/Decompiled/native/`.
- **CELT decoder DLL**: build stock CELT 0.7.1 for win-x64 (for example with
  [`../Native/celt/build-windows.sh`](../Native/celt/build-windows.sh)) and copy `celt071.dll` to `../Tools/bin/celt071_x64.dll`.

## Reference tools

The Python tools in `../Tools/` are the reference implementation of the protocol. They need Python 3 on Windows; the Bluetooth
client also works on Linux/BlueZ with `--channel`. `smartmike_bridge.py` also needs `numpy` and `sounddevice`.
**Pair the mic in the OS Bluetooth settings first.**

| Script | What it does |
|---|---|
| `swiss_protocol.py` | Frame encoder/decoder. Run it directly to self-test against packets hard-coded in the app |
| `smartmike_client.py` | Live test client: `list`, `probe`, `record`, `set`, `scan-channels`. Each run writes a capture folder under `../Captures/` |
| `qcelt_decode.py` | Decodes a recorded `audio.frames` capture to WAV |
| `smartmike_bridge.py` | Real-time: mic → CELT decode → any audio output device (e.g. a virtual cable), with drift compensation |

```bash
python Tools/smartmike_client.py list
python Tools/smartmike_client.py probe  28:36:38:xx:xx:xx                  # connect, handshake, log everything
python Tools/smartmike_client.py record 28:36:38:xx:xx:xx --seconds 10     # capture audio frames
python Tools/qcelt_decode.py Captures/<run>/audio.frames out.wav
python Tools/smartmike_bridge.py 28:36:38:xx:xx:xx --device "CABLE Input"  # live to a virtual cable
```

## Ground rules

- **Never install, flash or send firmware to a device**, and don't build CSR DFU parsers or extractors. The firmware docs
  describe the images; they are not tooling for changing them.
- Setting writes persist on the mic. After a hardware test, restore the defaults: **monitor volume 80, AGC on, mic gain 50**.
- `App/Decompiled/` is large: search it rather than reading whole trees.
- Record new findings in `../Docs/` with **Verified / Inferred / Open** and the evidence, and update [Plan.md](../Docs/Plan.md).
