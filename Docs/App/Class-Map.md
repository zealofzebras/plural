# Obfuscated class map

Paths are relative to `App/Decompiled/jadx/sources/`. jadx adds `/* compiled from: X.java */` comments that sometimes reveal the original file name. The Swiss SDK core was flattened into single-letter top-level packages (`a`–`r`) plus `d4`, `e4`, `l5`, `t3`, `v3`.

## Transport and session

| Class | Meaning |
|---|---|
| `k.e` | RFCOMM transport: `createRfcommSocketToServiceRecord(SPP)`, paced write queue (50 ms), raw read |
| `k.a` | Frame reader: reads `FF 5A` + length, checks "SS" → `provide.domain.a` (frame + timestamps) |
| `k.b` / `k.c` / `k.d` | Wrapper / interface / connect-state listener |
| `i.b`, `i.c` | Bluetooth discovery/profile listener (finds bonded device, triggers connect) |
| `h.b` (`h.a`) | Multi-device manager — two sessions (pos 0/1), routes `setParameter`/`write`/`readFullFrame` |
| `com.sabinetek.swiss.provide.domain.d` | Per-device session (`TAG = "SmartMikeDevice"`): codec, parser, read/decode threads, `setParameter` |
| `com.sabinetek.swiss.provide.domain.b` | Device info bean (= SDK `DevicesInfo`) |
| `p.a` / `p.e` | Device controller (= SDK `Swiss`): connect handshake, clock sync state machine, message dispatch 30000–30038, start/stop record |
| `p.b` | Firmware updater (= SDK `FirmwareUpdater`) — OTA over SPP |
| `p.c` | Per-device feature/state registry |

## Packet building (app → device)

| Class | Meaning |
|---|---|
| `m.d` → `m.c` → `m.b` | Frame wrapper (= SDK `PacketFormat`): `m.b.b` maps SDK key → (cmd, sub-type, value) |
| `m.a` | Command descriptor (cmd, sub-type, value, raw bytes) |
| `r.b` | Templates: link header `FF 5A 00 16 40 00 00 02 00`, sub-header `00 00 'S' 'S' 00 0C 00 00`, TLV `00 06 …`, 20-byte auth TLV |
| `r.a` | Generic frame builder (link header + sub-header + payload + checksums) |
| `r.c` | Builder factory by command; global seq (`f32059a`), last-rx seq (`f32060b`), sync base time (`f32061c`) |
| `l5.a` | 9-byte heartbeat |
| `l5.b` | DFU payloads |
| `l5.c` | NTP payload (u32 ms) |
| `l5.d` | Generic TLV parameter |
| `l5.e` | SN auth response |
| `e4.e` | Checksum (`a`, `b`, `d`), byte-array reverse, big-endian int read, version formatting |

## Packet parsing (device → app)

| Class | Meaning |
|---|---|
| `l.b` → `l.a` → `l.c` | Data parser (= SDK `AudioDispatcher` front end): header fields, checksum, command → handler |
| `n.c` | Parser factory: command → `o.*` handler |
| `n.a` | Parsed header (valid, total len, cmd, sub len, sub-type) |
| `o.c` | AUDIO_STREAM / AUDIO_STREAM_VER |
| `o.e` | CUR_VERSION |
| `o.o` | STATUS_INFO (battery, A2DP state, device time) |
| `o.d` | BUTTON_PRESSED / jack events |
| `o.m` | SN_AUTH_* |
| `o.l` | NTP_DELAY_REQ |
| `o.f` | DFU_DATA ack |
| `o.a`, `o.b`, `o.j`, `o.n`, `o.k`, `o.h`, `o.i`, `o.g` | AGC, ANS, MIC, SPK, MONITOR, EFFECT, EXTEND, EARSET reports |

## Enums

| Class | Meaning |
|---|---|
| `d4.o` | Command IDs |
| `d4.r` | Device type IDs (M_MIKE = 64 …) |
| `d4.f` | Codec plugin IDs |
| `d4.p` / `d4.s` / `d4.q` | MIC / SPK / MONITOR sub-types |
| `d4.c` / `d4.d` | AGC / ANS sub-types |
| `d4.j` / `d4.k` | EFFECT / EXTEND sub-types |
| `d4.m` / `d4.n` | LED array / LED patterns |
| `d4.g` | Button / jack events |
| `d4.t` | STATUS_INFO TLV types |
| `d4.b` | A2DP status |
| `d4.h` | OTA target (BLUETOOTH_OTA 0, CS_DSP_OTA 1) |
| `d4.i` | Earset mode page |
| `t3.b` | Audio bitrate (HIGH 190, MEDIUM 170, LOW 140) |
| `c.b` | Constants: SPP UUID, MAC prefixes, 44100 Hz, 2 ch, 512 frame, default codec SBC |
| `c.a` | Device type ID → display name |

## Codec

| Class | Meaning |
|---|---|
| `j.c` → `j.b` → `j.a` | Codec factory; `j.a.a` sniffs the frame header to pick SBC / SMARTQCELT / Opus and the sample rate |
| `a.b` (`a.b.a` enum) | Codec interface; enum SBC, OPUS, QCELT, SMARTQCELT |
| `a.d` | Codec implementation dispatching to the `jni.codec.*` classes |
| `jni.codec.CodecQCeltSmart` | `libjni-smartdecoder.so` |

## Other app classes

| Class | Meaning |
|---|---|
| `com.sabine.constants.a` | `Constants` — server URLs |
| `com.sabine.constants.MyApplication` | Application; picks API base by channel |
| `com.sabine.common.net.b` | `RetrofitUtils` |
| `m3.a` | Retrofit API interface |
| `com.sabine.update.d` | `FirmwareUtil` — firmware update check |
| `com.sabine.common.devices.a` | `DeviceConstants` — device names, server type IDs, 0.4.38 gate |
| `com.sabine.common.devices.h` | Device manager singleton (`h.S()`) |
| `com.sabinetek.swiss.provide.b` | Swiss provider singleton (`b.j()`) |
| `com.sabinetek.swiss.provide.utils.d` | Version compare, hex helpers |
| `com.sabinetek.swiss.sdk.SDKHelper` / `sdk.a` | SDK licence check / server auth calls |
