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

## What this means
- The mic **is** a standard Bluetooth headset. On Linux it appears as a normal card; HFP recording works but at narrowband (cut-off ≈ 3.5 kHz).
- High-quality audio only comes over the private SPP channel, compressed (QCELT and others) and decoded by the app.
- The app's SDK talks to the device over SPP UUID `00001101-…`; BLE `FFE0/FFE1` also exists in the app (which devices use it is **Open**).

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
- Does a real SmartMike+ advertise the same records and SPP on channel 14?
- ~~Does it support wideband HFP (mSBC)?~~ **Yes, on Windows 11.** 8 s recording from the standard "Headset (SmartMike+)" endpoint (2026-10-10, `Captures/*_hfp_headset.wav`): content is roughly flat to ~7 kHz with a ~18 dB cliff above 7 kHz, which is the mSBC (16 kHz) signature. Bowman's 3.5 kHz cut-off on Linux was narrowband CVSD, most likely because that stack did not negotiate mSBC. **Confirmed with speech** (`Captures/20261010_204156_hfp_headset_speech.wav`, peak −2.8 dBFS): energy stays at −16…−20 dB (relative to 300–1000 Hz) through 4–6 kHz, rolls off over 6–7.5 kHz, and is at the floor (−60…−73 dB) above 7.5 kHz. This is a 16 kHz-sampled mSBC channel.

## Practical options without any custom software
| Path | Quality | Notes |
|---|---|---|
| "Headset (SmartMike+)" (HFP, mSBC) | 16 kHz mono, voice-grade | Works in any app out of the box; switches the mic into call mode |
| SPP + `Tools/smartmike_bridge.py` | 44.1 kHz, CELT ~131 kbit/s | Full quality; needs the bridge (+ a virtual cable to appear as a mic) |
