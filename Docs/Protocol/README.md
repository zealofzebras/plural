# Bluetooth protocol — overview

## Advertised services (SDP)

From Don Bowman's SDP dump of a SmartMike *clone* ("SmartMic"). **Needs confirming on a real SmartMike+.**

| Service | UUIDs | Transport | Notes |
|---|---|---|---|
| Hands-Free unit | 0x111e, 0x1203 | RFCOMM ch 10 | HFP 1.6 |
| Headset | 0x1108, 0x1131, 0x1203 | RFCOMM ch 11 | HSP 1.2 |
| Audio Sink | 0x110b | L2CAP PSM 25 (AVDTP 1.3) | A2DP sink (audio *to* the mic's earpiece) |
| AV Remote / Target | 0x110e, 0x110f, 0x110c | L2CAP PSM 23 (AVCTP 1.4) | AVRCP |
| PnP Information | 0x1200 | — | |
| **Serial Port `M_MIKE_SLV`** | 0x1101 | **RFCOMM ch 14** | **Sabine private channel: control + high-quality mic audio** |

Class of device 0x240418 (audio / headphones).

### Real SmartMike+ on macOS (2026-10-26)

- SmartMike+ `28:36:38:34:0D:5E` (FW 0.4.37) on macOS 27: IOBluetooth's service list for the mic has no SPP (`0x1101`) record
  (HFP channel 10 and HSP channel 11 are listed), and an SDP query does not answer. Opening RFCOMM channel 14 directly works, so
  `MacBluetooth` falls back to it.
- IOBluetooth only delivered its callbacks once the calls ran on the main thread with the main run loop running; on a private
  run-loop thread the RFCOMM open never completed (`MainRunLoop`).
- With both, the CLI's normal `run` path connected, completed SESSION_OPEN and clock sync, applied monitor volume 80, AGC on and
  mic gain 50, and recorded a 10 s WAV take (897 audio frames, zero decode errors). No firmware commands were sent.

## What this means
- The mic **is** a standard Bluetooth headset. On Linux it appears as a normal card; HFP recording works but at narrowband (cut-off ≈ 3.5 kHz).
- High-quality audio only comes over the private SPP channel, compressed (QCELT and others) and decoded by the app.
- The app's SDK talks to the device over SPP UUID `00001101-…`; BLE `FFE0/FFE1` also exists in the app (which devices use it is **Open**).
- **iAP (MFi) over Bluetooth**: a plain string search of the SmartMike+ 0.4.32 firmware image finds a "Wireless iAP" SDP service name,
  the External Accessory protocol strings `com.sabinetek.iisrecorder` and `com.sabinetek.iLoveSound`, MFi identity fields
  (manufacturer "IDMIX Technology(HK)Co.", model "Sabine-S610") and `IAP:` debug logs (clock offset, button messages). So the mic
  appears to be an MFi accessory that iOS can reach through External Accessory. CoreBluetooth can't be used: it speaks only BLE
  GATT/L2CAP, and the mic SDK is SPP-only.
- **MetAudio iOS** (`SmartMike/App/Ipa/metaudio.ipa`, `com.sabinetek.app` 1.4.5, app binary "Lownia"). Its code and `__TEXT` are
  FairPlay-encrypted (cryptid 1), so only Info.plist and the symbol tables are readable:
  - Info.plist: `UISupportedExternalAccessoryProtocols` = `com.sabinetek.iisrecorder` only; `UIBackgroundModes` = `audio`, `external-accessory`.
  - Symbols (names only; behaviour not observed): `Swiss.framework` (`com.sabinetek.swiss`, built from `swiss-ios`) has the same class
    names as the Android Swiss SDK (`SWPacketParser parseFrame:`, `SWCommandConverter`, `SWQeltSmartDecoder`/`QCeltDecode`,
    `SWDeviceTimeSync`, `SWSyncTimeHandler`, the parameter handlers), so it looks like a port of it. A `Session` class has
    `openSession:protocol:`, `eaSession`, `setupConnectionInputStream:outputStream:`, `stream:handleEvent:`, `writeData:`,
    `readInputStreamData` and a `streamThread` property; `SWDeviceManager` references `EAAccessoryDidConnect/DidDisconnectNotification`.
  - **Verified on an iPhone 13** (iOS, 2026-10-26, FW 0.4.37) — the frames differ from SPP: iAP2 replaces the `FF 5A` link layer.
    Each EA message is a bare command frame `53 53 | len16 (= payload+6) | cmd16 | payload`, with no link header, `00 00` prefix or
    checksum; several can share one read and one can span reads. No session-open is needed: on `EASession` open the mic sends its state
    unprompted (`STATUS_INFO`, `CUR_VERSION`, `EFFECT_PARAM`, `MONITOR`, `ANS_PARAM`, `SN_AUTH_REPORT`, `BUTTON_PRESSED`); it ignores
    `FF 5A` frames. Clock sync, settings, `AUDIO_BEGIN` and audio (`0x0104` frames, 204 bytes) then work as over SPP, with no heartbeats.
    Plural's `AccessoryFraming` translates both ways so `MikeSession` runs unchanged.
  - `STBluetooth.framework` is a BLE SDK for Sabine gimbals (turnLeft/turnRight, face tracking), not for the mics.

## Protocol summary (from code)
- Framing: `FF 5A` sync, 9-byte link header (seq/ack), "SS" sub-header with a 16-bit command, TLV payload, two's-complement checksums — [SPP-Framing.md](SPP-Framing.md)
- Around 30 commands: version, status/battery, buttons, mic/monitor/EQ/AGC/ANS/effects, clock sync, audio start/stop, OTA — [Commands.md](Commands.md)
- Audio: `AUDIO_STREAM_VER` packets = 4-byte timestamp + QCELT (a CELT variant) frames, 44.1/48 kHz stereo, bitrate 140/170/190 — [Audio.md](Audio.md)
- No device authentication is enforced by the app — [Authentication.md](Authentication.md)
- Reference encoder/decoder: [`Tools/swiss_protocol.py`](../../Tools/swiss_protocol.py)

## Documents
- [SPP-Framing.md](SPP-Framing.md) — packet structure
- [Commands.md](Commands.md) — control commands
- [Audio.md](Audio.md) — audio stream
- [Authentication.md](Authentication.md) — handshake

## Open questions
- Why does macOS omit the SPP record for the tested SmartMike+, despite RFCOMM channel 14 carrying Swiss commands and audio?
  Are the advertised records/channel identical on other mics and hosts?
- ~~Does it support wideband HFP (mSBC)?~~ **Yes, on Windows 11.** 8 s recording from the standard "Headset (SmartMike+)" endpoint (2026-10-10, `Captures/*_hfp_headset.wav`): content is roughly flat to ~7 kHz with a ~18 dB cliff above 7 kHz, which is the mSBC (16 kHz) signature. Bowman's 3.5 kHz cut-off on Linux was narrowband CVSD, most likely because that stack did not negotiate mSBC. **Confirmed with speech** (`Captures/20261010_204156_hfp_headset_speech.wav`, peak −2.8 dBFS): energy stays at −16…−20 dB (relative to 300–1000 Hz) through 4–6 kHz, rolls off over 6–7.5 kHz, and is at the floor (−60…−73 dB) above 7.5 kHz. This is a 16 kHz-sampled mSBC channel.

## Practical options without any custom software
| Path | Quality | Notes |
|---|---|---|
| "Headset (SmartMike+)" (HFP, mSBC) | 16 kHz mono, voice-grade | Works in any app out of the box; switches the mic into call mode |
| SPP + `Tools/smartmike_bridge.py` | 44.1 kHz, CELT ~131 kbit/s | Full quality; needs the bridge (+ a virtual cable to appear as a mic) |
