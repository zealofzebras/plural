# SPP packet framing

**Status: Verified on hardware** (SmartMike+ `28:36:38:…`, FW 0.4.37, Windows 11 ARM64, 2026-10-10). Connected by SPP UUID (Windows resolved the channel via SDP). Session-open → CUR_VERSION in ~330 ms; clock sync and audio worked exactly as described below. Captures in `Captures/`. Reference implementation: [`Tools/swiss_protocol.py`](../../Tools/swiss_protocol.py) (self-test reproduces the app's hard-coded packets).

## Transport
- RFCOMM socket to SPP UUID `00001101-0000-1000-8000-00805F9B34FB` (`k.e.e()` → `createRfcommSocketToServiceRecord`). Clone SDP shows it on channel 14, service name `M_MIKE_SLV`.
- Up to **two devices** at once (positions 0 and 1 — stereo/TWS pair), each with its own socket and session (`provide.domain.d`).
- Writes are queued and paced: one queued packet every 50 ms (`k.e.a`), except when NTP sync is running. NTP and AUDIO_BEGIN-key packets bypass the queue.
- MAC prefix filter in the app: `12:FC:08:0…`, `28:36:38:3…` (`c.b.f11795a`); firmware contains `12fc08XXXXXX`.

All multi-byte integers are **big-endian**.

## Link header (9 bytes) — every packet

| Off | Size | Value | Meaning |
|---|---|---|---|
| 0 | 2 | `FF 5A` | Sync |
| 2 | 2 | n | Total packet length incl. header and trailing checksum |
| 4 | 1 | `40` | Constant (meaning Open) |
| 5 | 1 | seq | Sender sequence number, 0–255 wrapping (`r.c.f32059a`) |
| 6 | 1 | ack | Last `seq` received from the peer (`r.c.f32060b`, updated from byte 5 of every received packet) |
| 7 | 1 | type | `0` = heartbeat/ack only (9-byte packet), `1` = session control (see below), `2` = command frame |
| 8 | 1 | cksum | `(-sum(bytes[0..7])) & 0xFF` |

## Command frame (type 2)

| Off | Size | Value | Meaning |
|---|---|---|---|
| 9 | 2 | `00 00` | |
| 11 | 2 | `53 53` ("SS") | Sub-header magic — receiver drops frames without it |
| 13 | 2 | len | Payload length + 6 |
| 15 | 2 | cmd | Command ID — see [Commands.md](Commands.md) |
| 17 | len−6 | payload | Usually TLV(s) |
| n−1 | 1 | cksum | `(-sum(bytes[9..n-2])) & 0xFF` |

Receiver (`k.a.a`): read 4 bytes, require `FF 5A`, read the rest to `len`, require `len ≥ 13` and "SS" at 11–12; checksum verified when sub-len > 0 (`l.c.a`).

## TLV payloads
Most payloads are TLVs: `len16` (bytes in this TLV incl. the length field), `type16`, value. Status reports (`STATUS_INFO`) contain several TLVs back to back.

Generic parameter write (`l5.d`) — used by almost every setting:
```
00 06 | sub_type16 | value16
```
(The app computes high bytes as `x/255` — a bug; harmless for values < 255.)

## Session control packets (type 1) — hard-coded in `p.a`

| When | Bytes |
|---|---|
| 500 ms after socket connect (`X()`) | `FF 5A 00 1B 40 00 00 01 4B 40 40 00 11 EA 00 00 05 00 00 01 00 06 00 01 00 5D 1B` |
| On destroy / disconnect (`e0()`) | `FF 5A 00 16 40 59 EC 01 0B 40 40 00 0C EA 01 00 06 00 00 00 2F 54` |

Body pattern looks like `40 40 | len16 | EA <00=open / 01=close> | …`. Meaning of the remaining bytes is **Open**; replaying verbatim should be sufficient.

## Heartbeat (type 0)
`FF 5A 00 09 40 <seq> <ack> 00 <cksum>` — sent every 500 ms while audio is streaming (`l5.a`, message 30004), acknowledging received audio.

## Connection sequence (from `p.a` message handlers)
1. RFCOMM connect.
2. +500 ms: send session-open packet; start per-device read loop.
3. Device sends `CUR_VERSION` (6) → app learns name/type/versions.
4. App runs NTP-style clock sync (below). Device counts as "connected" only after version + sync (`domain.d.isConnected`).
5. Device may send `SN_AUTH_CHALLENGE` (32) — see [Authentication.md](Authentication.md).
6. Start audio: `AUDIO_BEGIN` (3) — see [Audio.md](Audio.md).

### Clock sync (`p.a.Z`, messages 30013–30016)
- App sends `NTP_SYNC` (cmd 20), payload `00 06 <u32 ms since base>`.
- Device replies `NTP_DELAY_REQ` (21). App replies `NTP_DELAY_RESP` (22) with its time; repeated until 4 rounds, then `NTP_DELAY_RESP_FINAL` (23).
- Timeout 1.5 s → sync failed. Base time = `elapsedRealtime` when the first device connected.
- Audio frame timestamps are relative to this base.

## Observed connect sequence (real device)
```
+0.51 s  TX SESSION_OPEN
+0.84 s  RX CUR_VERSION        type 64 (M_MIKE), protocol 0.0.88, FW 0.4.37, HW 0.1.1
         RX EFFECT_PARAM       reverb ratio 60, music mixer 0, DSP sig-flow 0x50, sound effect 0
         RX MONITOR            both channels 80
         RX ANS_PARAM          ANS on
         RX SN_AUTH_REPORT     serial number (24 ASCII chars)
         RX BUTTON_PRESSED     JACK_UNPLUGGED
         TX/RX clock sync (4 rounds, ~130 ms each)
+1.37 s  TX AUDIO_BEGIN 4/190
+2.22 s  RX MEDIA_INFO, first AUDIO_STREAM_VER at +2.29 s
```
The device sends its state unprompted after the session-open. No `SN_AUTH_CHALLENGE` was sent, and audio streamed without any authentication.

## Open questions
- Meaning of header byte 4 (`0x40`) and of session-control body bytes.
- ~~Is session-open required?~~ **No** for audio. Without it the device sends no state dump (no CUR_VERSION), and its sequence numbers continue from the previous session instead of restarting at 1. `AUDIO_BEGIN` still works. So session-open (re)initialises the session and triggers the state report.
- ~~Is clock sync mandatory?~~ **No.** Without it audio still streams; timestamps then use the device's own clock base.
- `STATUS_INFO` (battery mV + A2DP state) arrives periodically, every ~5 s, during streaming.
