# Plan and status

## Phase 0 — Tooling and collection
- [x] Collect APK (MetAudio 1.5.2) and decompile with jadx → `App/Decompiled/jadx`
- [x] Collect Sabinetek Swiss SDK sample → `App/Reference/SwissSample-android`
- [x] Collect official PC firmware updater, extract SmartMike+ image 0.4.32_0702 → `Firmware/Images`
- [x] Ghidra 12.1.4 (headless) → `App/Decompiled/native/libjni-smartdecoder.so.c`
- [x] Zig 0.13 as C compiler; CELT 0.7.1 → `Tools/bin/celt071_x64.dll`

## Phase 1 — Static analysis of the protocol (APK)
- [x] Map SDK classes onto obfuscated APK classes → [App/Class-Map.md](App/Class-Map.md)
- [x] SPP connection setup and packet framing → [Protocol/SPP-Framing.md](Protocol/SPP-Framing.md)
- [x] Command IDs, settings keys, sub-types, report formats → [Protocol/Commands.md](Protocol/Commands.md)
- [x] Audio start/stop, stream packet layout, codec detection → [Protocol/Audio.md](Protocol/Audio.md)
- [x] Authentication: not enforced → [Protocol/Authentication.md](Protocol/Authentication.md)
- [x] Reference frame encoder/decoder → `Tools/swiss_protocol.py`
- [x] QCELT frame header + CELT version (0.7.1) from Ghidra
- [ ] Meaning of the session-open/close (type 1) packets
- [ ] Exact DFU_BEGIN/END payload (check smali of `l5.b`)

## Phase 2 — Dynamic confirmation (SmartMike+ FW 0.4.37, HW 0.1.1, on this PC)
- [x] Connect via SPP UUID on Windows
- [x] Handshake: session-open → CUR_VERSION + state reports
- [x] Clock sync
- [x] AUDIO_BEGIN → `AUDIO_STREAM_VER` captured (`Captures/*_record_*`)
- [x] Decode capture to WAV (`Tools/qcelt_decode.py`, 0 errors) — listening check pending
- [x] Settings writes (monitor, mic gain, AGC) — work and persist; not acknowledged
- [x] Without clock sync / without session-open — audio still works
- [x] MEDIUM/LOW (→ 180/150 B per frame); SBC request ignored
- [ ] 48 kHz (no request parameter found; the device chooses 44.1 kHz)

## Phase 3 — Computer-side bridge (main goal)
- [x] Python RFCOMM client → `Tools/smartmike_client.py`
- [x] Real-time decode → output device (`Tools/smartmike_bridge.py`, drift-compensated, 0 underruns in 40 s)
- [x] Settings control from the bridge (`--set`)
- [x] Audio out: standard A2DP "Headphones (SmartMike+)" works alongside the SPP stream
- [ ] Install a virtual audio cable (needs ARM64 driver support) so apps can record the bridge as a microphone
- [ ] Optional: tray app / auto-reconnect / run at startup

## Phase 3b — Plural app (C# / Avalonia) → [Plural/Design.md](Plural/Design.md)
- [x] Core engine + tests (ARM64 + x64)
- [x] Windows platform layer (RFCOMM, WASAPI)
- [x] CLI runner, verified on hardware
- [x] UI v1: mic cards, settings, Record All
- [x] Recording formats (WAV, M4A, MP3, MKA, MKV, MP4), camera video, UI redesign with persisted settings: built, 46 Core tests pass (see [Plural/Recording.md](Plural/Recording.md))
- [x] Windows video: 1080p30 H.264 verified on Surface arm64 + x64
- [ ] Hardware test of the new formats and camera with real mics (Windows and Pixel 8); Android camera and AAC not yet run
- [ ] Measure audio-video latency (clap test) and compensate; verify MKV rotation sign in players
- [ ] Hardware test of the UI with the user
- [ ] Install a virtual cable (VB-CABLE has ARM64 since Oct 2024) and verify a mic appears in another app
- [ ] Test with 2+ SmartMikes simultaneously (Bluetooth bandwidth)
- [ ] Packaging (self-contained win-arm64 / win-x64 builds), app icon, auto-reconnect
- [x] macOS platform layer (IOBluetooth, Core Audio, AAC) and iOS head (External Accessory, audio session): built, compile-checked
- [ ] Run macOS on a Mac with a SmartMike; find out whether the SmartMike+ is MFi (iOS needs its External Accessory protocol string)
- [ ] Camera on macOS/iOS (AVFoundation + VideoToolbox); RFCOMM server on macOS for Bluetooth remote mode
- [ ] Own virtual-mic driver (later), Linux platform layer (later)

## Phase 4 — Firmware (stretch, risk of bricking)
- [x] Try to get newer images from the server API — all Sabinetek API servers are offline; device's 0.4.37 is the newest known
- [ ] Parse `CSR-dfu2` / `fsr_dfu1` and list contained files → [Firmware/DFU-Format.md](Firmware/DFU-Format.md)
- [ ] Determine whether image is encrypted/signed
- [ ] Identify hardware recovery path (CSR SPI debug port) before any modification
- [x] Wideband HFP (mSBC): already works on Windows 11 with the stock firmware — no change needed

Note: standard Classic Bluetooth has no profile for high-quality audio *from* a headset; firmware changes can at best improve HFP to 16 kHz wideband. Full quality requires the bridge (Phase 3).
