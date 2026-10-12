# MetAudio app — overview

| Item | Value | Status |
|---|---|---|
| Package | `com.sabinetek.app` | Verified (`SmartMike/App/Decompiled/jadx/resources/AndroidManifest.xml`) |
| Version | 1.5.2 | Verified |
| APK SHA-256 | `69eb239a8545ea71772077c71677418a9460462e6093b19b2204ca046b91dcab` | |
| Build channel | `google` (Play build → uses `https://sapi.sabinetek.com`) | Inferred (APKPure mirrors Play) |
| Obfuscation | R8/ProGuard; app classes renamed to `a`, `b`, … but package names kept | Verified |

## Main code areas

| Package | Purpose |
|---|---|
| `com.sabinetek.swiss.*` | Sabine "Swiss" device SDK — Bluetooth connection, packet handling, codecs, firmware update. Same SDK as the public SwissSample (v1.4.14) but newer and obfuscated. |
| `com.sabinetek.swiss.jni.*` | JNI wrappers (names not obfuscated): `codec.CodecQCelt`, `codec.CodecQCeltSmart`, `codec.CodecSbc`, `codec.CodecOpus`, `buffer.JitterBuffer`, `buffer.ExpendBuffer`, `noisesuppression.NoiseSuppression`, `resample.Resample`, `beamformer.Beanformer` |
| `com.sabine.common.devices` | Device manager / device constants (names, type IDs) |
| `com.sabine.devices.smartmike` | SmartMike+ settings UI (`SmartMikeSetActivity`), scene, bean |
| `com.sabine.update` | App and firmware update check + download (`FirmwareUtil`, `FirmwareBean`) |
| `com.sabine.common.net`, `m3.a` | Retrofit client and backend API interface — see [Server-API.md](Server-API.md) |

## Native libraries (`SmartMike/App/Decompiled/unzipped/lib/<abi>/`)

| Library | Likely role |
|---|---|
| `libjni-qcelt.so` | Qualcomm/CSR CELT codec — mic audio from CSR devices (Inferred) |
| `libjni-smartdecoder.so` | Used by `CodecQCeltSmart` — SmartMike-specific decode path (Inferred) |
| `libjni-sbc.so` | SBC codec — likely monitor/return audio (Inferred) |
| `libjni-opus.so` | Opus codec — other device families (Inferred) |
| `libjni-authenticate.so` | Device authentication handshake — see [../Protocol/Authentication.md](../Protocol/Authentication.md) |
| `libjni-jitterbuffer.so` | Jitter buffer for the Bluetooth audio stream |
| `libjni-noisesuppression.so`, `libjni-resample.so` | Phone-side DSP |
| ffmpeg / mp4v2 / lame / fdk-aac / flac | Recording and export, not device related |

## Supported devices (from `com.sabine.common.devices.a`)

| Name string | Type enum | Server `type` |
|---|---|---|
| `SmartMike+` | `TYPE_SMARTMIKE` | 1 |
| `SmartMike Classic` | `TYPE_SMARTMIKE_2` | 8 |
| `SmartMikeSilver` | `TYPE_SILVER` | 501 |
| `AudioWow` | `TYPE_AUDIOWOW` | 2 |
| `Saramonic-BTW` | `TYPE_BTW` | 3 |
| `AudioWow S1` | `TYPE_SINGMIC` | 6 |
| `AudioWow B2` | `TYPE_FINALACE` | 7 |

Other names listed without a type: Sabine ALAYA, ALAYA Pro, SMIC, SOLO, ARENA, SmarterMike+, Hohem SmartMic (sent as type 0), AudioWow P2, OnyxGO, Xiaomi K-song, Edifier W800K.

## Bluetooth UUIDs found in the dex

| UUID | Meaning |
|---|---|
| `00001101-0000-1000-8000-00805F9B34FB` | SPP (RFCOMM serial) — main device channel |
| `0000ffe0-…` / `0000ffe1-…` | Generic BLE serial service / characteristic — used only in `com.sabinetek.cameraman.x` (camera accessory code), not by the mic SDK |
| `00002902-…` | BLE CCCD (notifications) |

## Firmware-version gates in the app

| Version | Effect | Where |
|---|---|---|
| ≥ 0.4.26 | Different protocol behaviour | `com/sabinetek/swiss/provide/domain/d.java` (lines ~138, 444, 537) |
| ≥ 0.4.31 | SmartMike+ noise suppression handled differently | `domain/d.java` ~576 |
| ≥ 0.4.38 | Advanced settings UI enabled | `com/sabine/common/devices/a.java:68`, `SmartMikeSetActivity.java:150` |

## Answered
- The mic SDK uses **SPP only** (`k.e`); BLE belongs to camera accessory code.
- `CodecQCeltSmart` = QCELT decoder + jitter buffer + timestamped frames, used for firmware ≥ 0.4.26; plain `CodecQCelt` for older firmware. See [../Protocol/Audio.md](../Protocol/Audio.md).

The obfuscated SDK core lives in single-letter packages — see [Class-Map.md](Class-Map.md).
