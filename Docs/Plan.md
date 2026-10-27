# Plan and status

## Phase 0 — Tooling and collection
- [x] Collect APK (MetAudio 1.5.2) and decompile with jadx → `SmartMike/App/Decompiled/jadx`
- [x] Collect Sabinetek Swiss SDK sample → `SmartMike/App/Reference/SwissSample-android`
- [x] Collect official PC firmware updater, extract SmartMike+ image 0.4.32_0702 → `SmartMike/Firmware/Images`
- [x] Ghidra 12.1.4 (headless) → `SmartMike/App/Decompiled/native/libjni-smartdecoder.so.c`
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
- [x] Hardware test of the new formats and camera (Surface: all six formats with pause; Pixel 8: MP4/MKV with pause, front and rear camera; 2026-10-13)
- [ ] Measure audio-video latency (clap test) and compensate; verify MKV rotation sign in players
- [ ] Hardware test of the UI with the user
- [ ] Install a virtual cable (VB-CABLE has ARM64 since Oct 2024) and verify a mic appears in another app
- [ ] Test with 2+ SmartMikes simultaneously (Bluetooth bandwidth)
- [ ] Packaging (self-contained win-arm64 / win-x64 builds), app icon
- [x] macOS platform layer (IOBluetooth, Core Audio, AAC) and iOS head (External Accessory, audio session): built, compile-checked
- [x] Run macOS on a Mac with a SmartMike (CLI and app, 2026-10-26)
- [x] SmartMike+ is MFi: firmware has iAP; MetAudio iOS 1.4.5 declares only `com.sabinetek.iisrecorder`; over EA the mic sends bare "SS" frames (no `FF 5A` link layer, Protocol/README.md)
- [x] Test the iOS External Accessory connection on an iPhone (iPhone 13: connects, streams and records a SmartMike take)
- [x] Camera on macOS/iOS (AVFoundation + VideoToolbox): implemented, compiled only
- [ ] Test the camera on a Mac (built-in, USB webcam) and an iPhone (main, front; rotation)
- [ ] RFCOMM server on macOS for Bluetooth remote mode
- [ ] Own virtual-mic driver (later), Linux platform layer (later)

## Phase 3c — Tier 1: take library, player, recording reliability → [Plural/Recording.md](Plural/Recording.md)
Built (2026-10-12); hardware tests and fixes 2026-10-13. 350 Core tests pass on win-arm64 and win-x64.
- [x] Take library: scan, `plural.json` sidecar, rename and notes, soft delete with Undo, Recently deleted, purge after 30 days, poster, peaks
- [x] Managed readers (WAV, Matroska, MP4 progressive and fragmented, MP3) and FLAC decoder; platform decoders (MF, MediaCodec, AudioConverter)
- [x] `TakePlayer`: audio master clock, per-track gain, mute and solo, remote offsets, video decode with late-frame drop; `VideoLayout` and `SpotlightFollower`
- [x] Navigation (Recorder, Takes, Player), Takes page, Player page with waveforms and video layout
- [x] Pause / resume: local, remote (Hello level 2) and CLI `pause`/`resume`; key frame after resume (video gap 34 ms Surface, 42 ms Pixel 8; 2026-10-13)
- [x] Fragmented MP4/M4A writer; Windows AAC priming measured = 0; Android AAC priming measured = 2048 (Pixel 8, Tab P10)
- [x] Recovery on launch (`recovered` status, WAV and MKV header repair)
- [x] Free-space estimate, auto-stop at 200 MB, mic and host battery warnings
- [x] SmartMike auto-reconnect (backoff 1, 2, 4, 10 s; dropouts in the sidecar)
- [x] SmartMike button events logged on hardware (single button: short press = `MFB_SINGLE`, used as Record); Record toggle mapped in `MikeHub`
- [x] Android foreground service (`RecordingService`) with Pause/Stop notification actions, built; notification and screen-off recording seen on Pixel 8 (2026-10-13)
- [ ] Android build of Tier 1 (release APK) — not verified yet
- [ ] Surface and Pixel 8: take with 2+ mics (one SmartMike) and video, pause and resume, playback with waveforms and synced video, rename, delete, restore (2026-10-13: on Pixel 8 takes, Player, rename, delete and undo work; the rest not recorded)
- [ ] Pixel 8: record 10 min with the screen off, local mic and SmartMike; check the notification and the Pause/Stop actions (2026-10-13: 118 s take incl. 30 s screen-off; 10 min not run)
- [ ] Android 17: video in the background only when the service started while visible — check and document
- [x] Measure AAC priming: Android 2048 (Pixel 8, Lenovo Tab P10); Apple still 1024 assumed
- [ ] Pixel 8: MediaCodec decoders and player (AAC and H.264 playback): the Player works (takes, rename, delete, undo); each codec's playback not separately recorded
- [x] Forced kill during an MP4 take leaves a playable file (Surface: killed at 8.0 s, 8.05 s video / 7.96 s audio, plays; Pixel 8: force-stop → Recovered)
- [ ] Check fMP4 in VLC, QuickTime, Windows Media Player/Photos, Android Photos/Files, DaVinci Resolve, Premiere; add a Finalize step only if an editor rejects it
- [ ] SmartMike reconnect: switch a mic off for 10 s mid-take, then on; clap before and after, track stays aligned
- [ ] SmartMike Record button starts and stops a real take (button tested; the record path not yet)
- [ ] Pause with real mics and camera: tracks and video stay aligned after resume (video gap after resume 34 ms Surface, 42 ms Pixel 8; track alignment not measured)
- [ ] Free-space auto-stop at 200 MB (dry run on a small disk)
- [ ] Remote: 3-video take on phone portrait, phone landscape and desktop shows the expected layout; Pause between two devices (verified PC → Pixel 8 over Wi‑Fi, 2026-10-13); "Doesn't support pause" with a level-1 peer
- [ ] Tablet and desktop Takes and Player layouts with real takes
- [ ] macOS and iOS: playback decodes (audio + VideoToolbox video) compiled only
- [x] 2026-10-13 fixes (tested): Android video orientation (Pixel 8, front and rear), key frame after resume, MKA/MKV duration, file-name sanitizing, remote pending takes expire after 24 h, Connect mics skips HFP inputs, landscape and mixer layouts (Tab P10, Pixel 8)

## Phase 4 — Firmware (stretch, risk of bricking)
- [x] Try to get newer images from the server API — all Sabinetek API servers are offline; device's 0.4.37 is the newest known
- [ ] Parse `CSR-dfu2` / `fsr_dfu1` and list contained files → [Firmware/DFU-Format.md](Firmware/DFU-Format.md)
- [ ] Determine whether image is encrypted/signed
- [ ] Identify hardware recovery path (CSR SPI debug port) before any modification
- [x] Wideband HFP (mSBC): already works on Windows 11 with the stock firmware — no change needed

Note: standard Classic Bluetooth has no profile for high-quality audio *from* a headset; firmware changes can at best improve HFP to 16 kHz wideband. Full quality requires the bridge (Phase 3).
