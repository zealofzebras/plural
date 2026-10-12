# Audio stream

**Status: Verified on hardware (SmartMike+, FW 0.4.37, 2026-10-10); frame header recovered from `libjni-smartdecoder.so` with Ghidra.**

## Starting / stopping
- `AUDIO_BEGIN` (cmd 3), payload `00 06 | 00 04 | 00 <bytes-per-frame>`; sub-type `4` = `CODEC_PLUGIN_CO_QCELT`. A value of `0` selects SBC instead (sub-type `1` = `CODEC_PLUGIN_CO_SBC`). Firmware < 0.4.9 always gets 0.
- Quality enum `t3.b`: **HIGH = 190, MEDIUM = 170, LOW = 140**. These are **CELT bytes per 512-sample frame**, not kbps. At 44.1 kHz: 190 B → 130.9 kbit/s, 170 → 117.1, 140 → 96.5 (plus 8 bytes/frame overhead).
- **Observed:** the device does not use the requested value exactly. HIGH 190 → 190 B, MEDIUM 170 → **180 B**, LOW 140 → **150 B** per frame (header byte 3 and `MEDIA_INFO` carry the real value). Requesting `0` (SBC) is ignored on FW 0.4.37; the device keeps sending QCELT 190.
- `AUDIO_END` (cmd 5), same payload.
- While streaming, the app sends a 9-byte heartbeat/ack every 500 ms ([SPP-Framing.md](SPP-Framing.md#heartbeat-type-0)). The device answers `AUDIO_END` with a heartbeat of its own.
- After `AUDIO_BEGIN` the device sends `MEDIA_INFO` (cmd 7). Observed: TLVs `type0 = 0x01B9` (441 → 44.1 kHz), `type1 = 2` (channels), `type2 = 4` (codec plugin CO_QCELT), `type3 = 0xBE` (190 bytes/frame).

Codec plugin IDs (`d4.f`): CELT 0, CO_SBC 1, CO_CELT 2, SBC 3, CO_QCELT 4, CO_KQCELT 5.

## Stream packets (device → app)
- `AUDIO_STREAM_VER` (cmd 260 = `0x0104`) on current firmware; `AUDIO_STREAM` (cmd 4) on old firmware.
- Observed at HIGH: every packet = 215 bytes on the wire, payload 198 bytes, **one 512-sample stereo frame**, 86.13 packets/s (exactly 44100/512). Timestamps advance 11–13 ms per packet.

| Payload off | Size | Meaning |
|---|---|---|
| 0 | 4 | Timestamp, ms since clock-sync base (u32 BE; app patches wrap-around) |
| 4 | 4 | QCELT frame header (below) |
| 8 | n | CELT 0.7.1 frame, n = header byte 3 |
| … | | Further header+frame pairs may follow (decoder loops) |

## QCELT frame header (from `CQCelt_Decoder::unpack_frame_header` / `decode`)

| Byte | Bits | Meaning |
|---|---|---|
| 0 | | `0x9E` sync |
| 1 | 7–6 | Sample rate index → table `{16000, 32000, 44100, 48000}` |
| 1 | 5–4 | Channels: `0` = mono, `1` = stereo, others invalid |
| 1 | 5, 2 | Must be 0 (`& 0x24`) |
| 1 | 2–0 | Frame-size index → table `{128, 256, 512, 1024}` (must be < 4) |
| 2 | | Not used by the decoder; observed `00` and `03` |
| 3 | | CELT bytes in this frame (190/170/140) |

Observed header `9E 92 00 BE` → 44.1 kHz, stereo, 512 samples, 190 bytes.

`checkparameter` accepts 44.1 kHz only with frame sizes 256/512, and 48 kHz with 64/128/240/256/512.

## CELT version: 0.7.1 (stock)
- The decoder calls `celt_mode_create(rate, frame_size, &err)`, `celt_decoder_create(mode, channels, &err)` and `celt_decode(st, data, len, pcm)` with 4 arguments. That API only exists in CELT 0.7–0.10; 0.11 added a frame-size argument.
- The internal function set (`compute_pitch_gain`, `apply_pitch`, `folding_decision`, `intra_fold`, `quant_prob_alloc`, `intra_decision`, `ec_laplace_get_start_freq`, `quant_bands_stereo` …), with every function prefixed `qc_`, all exist in **CELT 0.7.1**. Pitch prediction was removed in 0.8.
- Source: `https://downloads.xiph.org/releases/celt/celt-0.7.1.tar.gz`, built as `Tools/bin/celt071_x64.dll` (Zig, x86_64-windows).
- Decoder: [`Tools/qcelt_decode.py`](../../Tools/qcelt_decode.py).
- **Verified:** a 5.1 s capture (443 frames) decoded with stock CELT 0.7.1 with 0 errors. Output has a plausible noise floor (≈ −45 dBFS), clear transients and natural spectral roll-off. **L and R are identical**: the SmartMike+ sends its mono capsule duplicated into a stereo stream.

## Quality comparison with speech (2026-10-10)
Same speaker and mic, spectrum of the loudest 30 % of 50 ms segments, relative to 300–1000 Hz:

| Band | SPP / QCELT 190 (`Captures/20261010_204408_record_*`) | HFP mSBC (`Captures/20261010_204156_hfp_headset_speech.wav`) |
|---|---|---|
| 4–6 kHz | −22 / −31 dB | −16 / −20 dB |
| 8–10 kHz | −39 dB | −74 dB |
| 10–16 kHz | −38 … −48 dB | floor |
| 16–20 kHz | −51 … −52 dB | floor |
| > 20 kHz | −71 dB | floor |

SPP delivers full-band audio to ~20 kHz; HFP is limited to ~7.5 kHz. 10 s capture: 789 frames, 0 decode errors, ~130 kbit/s.

## Codec detection in the app (`j.a.a`, on payload bytes)

| Bytes [4],[5] | Codec | Notes |
|---|---|---|
| `9C BD` | SBC | `0x9C` = standard SBC sync word |
| `9E 92` or `9E D2` | **SMARTQCELT** (FW ≥ 0.4.26) | `0xD2` = 48 kHz stereo 512 |
| `[0]=9D [1]=97` | Opus | |
| otherwise | Opus (or SBC with repaired sync if `[1]=BD [2]=37`) | |

## App-side decoder stack (`libjni-smartdecoder.so`)
`CQelt_SmartDecoder` sits on top of `CQCelt_Decoder`. It adds a jitter buffer, packet-loss detection by timestamp (`find_packet_loss_pos`, `on_process_packet_loss`), WSOLA time stretching (`CWsola`, `CWSolaEngine`) and sample-rate arbitration between two mics (`CSraPreDelay`, `sra_calculate`). None of this is required to get audio; it's needed for perfect timing and for dual-mic sync. Ghidra output: `SmartMike/App/Decompiled/native/libjni-smartdecoder.so.c`.

## Phone-side post-processing
Noise suppression (`libjni-noisesuppression`), optional extra gain, stereo→mono beamformer, resampling. Not needed for raw capture.

## Audio out (computer → mic headphone jack) — standard A2DP, Verified
- Windows exposes the mic's A2DP sink as **"Headphones (SmartMike+)"**. Playing to it while the SPP mic stream runs works: the device reported `STATUS_INFO A2DP_STATUS = 0x81` (STREAMING) during playback and `0x82` (END) afterwards. The SPP stream kept 86 frames/s; only one buffer underrun occurred, at the moment A2DP started.
- The "monitor" volume (`MONITOR` command) is the device mixing its own mic into the headphones locally.
- No proprietary phone→mic audio path is needed. (`CQCelt_Encoder` exists in the library but is unused for this.)

## Real-time bridge
[`Tools/smartmike_bridge.py`](../../Tools/smartmike_bridge.py) decodes the stream live into any Windows output device. It uses a jitter buffer with drift compensation (resampling ratio steered by buffer level, max ±0.5 %): the mic's clock runs about 0.2 % slow against this PC's sound card. 40 s test: buffer 46–91 ms, 0 underruns, 0 decode errors.
