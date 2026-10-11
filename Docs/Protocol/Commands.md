# Commands

**Status: reconstructed from code; CUR_VERSION, EFFECT_PARAM, MONITOR, ANS_PARAM, SN_AUTH_REPORT, BUTTON_PRESSED, MEDIA_INFO, NTP_*, AUDIO_* verified on hardware.** Settings writes **verified**: they are not acknowledged, but take effect immediately and **persist across connections** (the device reports the new value after the next session-open).

### Hardware test results (FW 0.4.37)
| Setting | Result |
|---|---|
| `MONITOR` both = 60 | Device reported `00 3C` on the next connect; restored to 80 |
| `MIC_PARAM` both (mic gain), AGC off | Gain 20 → room noise −80 dBFS; gain 90 → −35 dBFS (≈ 0.65 dB per step) |
| `AGC_PARAM` on, gain 20 | Room noise −60.6 dBFS (AGC lifts quiet input ~20 dB). The device was originally in AGC mode; restored AGC on, gain 50 |
| Mic gain / AGC state | **Not reported** by the device on connect (only EFFECT, MONITOR, ANS are) | Sources: command enum `d4.o`, key→command mapping `m.b.b` (decompiled with `--show-bad-code`), sub-type enums `d4.*`, payload builders `l5.*`, parsers `o.*` / factory `n.c`.

## Command IDs (`d4.o`)

| ID | Name | Direction | Payload / handler |
|---|---|---|---|
| 0 | DFU_BEGIN | app→dev | `l5.b` — see [../Firmware/Update-Methods.md](../Firmware/Update-Methods.md) |
| 1 | DFU_DATA | both | app→dev: raw 128-byte chunk; dev→app: u32 at 17 (`o.f`) |
| 2 | DFU_END | app→dev | `l5.b`, value = byte-sum of image |
| 3 | AUDIO_BEGIN | app→dev | TLV `type=codec plugin`, `value=bitrate` |
| 4 | AUDIO_STREAM | dev→app | Audio (older firmware, no timestamp) |
| 260 (0x104) | AUDIO_STREAM_VER | dev→app | Audio with 4-byte timestamp |
| 5 | AUDIO_END | app→dev | Same as AUDIO_BEGIN |
| 6 | CUR_VERSION | dev→app | Version report (`o.e`) — below |
| 7 | MEDIA_INFO | dev→app | Not parsed in 1.5.2. Observed after AUDIO_BEGIN: TLVs 0 = 441 (sample rate/100), 1 = 2 (channels), 2 = 4 (codec plugin), 3 = 190 (bytes/frame) |
| 8 | BUTTON_PRESSED | dev→app | Event code at bytes 19–20 (`o.d`) — below |
| 9 | STATUS_INFO | dev→app | TLV list (`o.o`) — below |
| 10 | STATUS_ERROR | dev→app | Not parsed |
| 11 | EXTEND_STATUS | dev→app | |
| 12 | MIC_PARAM | both | |
| 13 | SPK_PARAM | both | |
| 14 | MONITOR | both | |
| 15 | AGC_PARAM | both | |
| 16 | ANS_PARAM | both | |
| 17 | EFFECT_PARAM | both | |
| 18 | EXTEND_PARAMTERS | both | |
| 19 | HEARING_AID | app→dev | |
| 20 | NTP_SYNC | app→dev | `00 06 u32` ms |
| 21 | NTP_DELAY_REQ | dev→app | |
| 22 | NTP_DELAY_RESP | app→dev | `00 06 u32` ms |
| 23 | NTP_DELAY_RESP_FINAL | app→dev | `00 06 u32` ms |
| 24 | PANIC_REPORT | dev→app | |
| 32 | SN_AUTH_CHALLENGE | dev→app | See [Authentication.md](Authentication.md) |
| 33 | SN_AUTH_RESPONSE | app→dev | 20 bytes: `00 14 00 00` + 16-byte response (`l5.e`) |
| 34 | SN_AUTH_REPORT | dev→app | Serial number |
| 48 | EARSET_MODE | both | |

## Settings: SDK key → command / sub-type (`m.b.b`)

All use the generic TLV `00 06 | sub16 | value16` unless noted. "Key" is the internal SDK parameter number passed to `setParameter(key, value)`.

| Key | Setting | Cmd | Sub-type |
|---|---|---|---|
| 3 / 4 | Audio begin / end | AUDIO_BEGIN / AUDIO_END | `1` CO_SBC if value 0, else `4` CO_QCELT; value = bitrate |
| 5 / 6 / 7 | Mic gain both / L / R | MIC_PARAM | 2 / 0 / 1 |
| 8 / 9 / 10 | Speaker volume both / L / R | SPK_PARAM | 2 / 0 / 1 |
| 11 / 12 / 13 | Monitor volume both / L / R | MONITOR | 2 / 0 / 1 |
| 132 | Monitor mute | MONITOR | 3 |
| 14 | AGC on/off | AGC_PARAM | 0 (ATTACK=1, DECAY=2 also defined) |
| 15 | ANS on/off | ANS_PARAM | 0 |
| 34 | ANS level | ANS_PARAM | 1 |
| 16 | Reverb ratio | EFFECT_PARAM | 1 (REVB_TYPE=0) |
| 17 | Music mixer | EFFECT_PARAM | 2 (MUSIC_BACKOFF=3) |
| 100 | Devocal auto | EFFECT_PARAM | 4 (value shifted `<<4`) |
| 101 | DSP signal-flow bitmap (scene) | EFFECT_PARAM | 5 |
| 26 | Sound effect | EFFECT_PARAM | 6 |
| 18–21 | LED pattern invalid/close/twinkle/breathing | EXTEND_PARAMTERS | 0, value 0/1/2/3 |
| 35–39 | LED array close/white/dyn-white/colored/dyn-colored | EXTEND_PARAMTERS | 11, value 0–4 |
| 28 | Event indicator ("twinkle mode") | EXTEND_PARAMTERS | 5 |
| 29 | App work state | EXTEND_PARAMTERS | 6 (UNSPECIFIED=0, FOREGROUND=1, BACKGROUND=2) |
| 30 / 31 | Single / double press quick-set | EXTEND_PARAMTERS | 7 / 8 |
| 32 | OP relay | EXTEND_PARAMTERS | 9 |
| 33 | Voice prompt | EXTEND_PARAMTERS | 10 |
| — | Sports-safe / app-launch-promote / low-cut | EXTEND_PARAMTERS | 1 / 2 / 3 (no key found yet) |
| 40 / 41 | Earset mode setting / select | EARSET_MODE | 1 / 2 (REPORT=0) |
| 98 | Ext-mic auto-select | MIC_PARAM | 50 |
| 99 | Mic/speaker active bitmap | MIC_PARAM | 49 |
| 131 | Mic preset EQ | MIC_PARAM | 3 |
| 50 | Speaker preset EQ | SPK_PARAM | 3 |
| 1000–1005 | Mic user EQ master / band 1–5 gain | MIC_PARAM | 4, 6, 9, 12, 15, 18 |
| 2000–2005 | Speaker user EQ master / band 1–5 gain | SPK_PARAM | 4, 6, 9, 12, 15, 18 |
| 51 | Hearing aid on/off | HEARING_AID | 0 |
| 22 / 24 / 25 | NTP sync / delay resp / final | 20 / 22 / 23 | — |
| 27 | SN auth response | SN_AUTH_RESPONSE | — |

EQ band F/Q sub-types also exist (F: 5, 8, 11, 14, 17; Q: 7, 10, 13, 16, 19) but no key uses them in 1.5.2.

## Device → app reports

### CUR_VERSION (`o.e`) — frame length must be 60
Six TLVs of `00 06 | type16 | value16` starting at offset 17:

| Value offset | Field |
|---|---|
| 21–22 | Device type ID (`d4.r`) |
| 27–28 | Protocol version |
| 33–34 | Firmware version |
| 39–40 | Hardware version |
| 45–46 | Codec version |
| 51–52 | Manufacturer |
| 57–58 | Licensed-for |

Version encoding (`e4.e.c`): `major = v>>12`, `minor = (v>>8)&0xF`, `patch = v&0xFF` → e.g. `0x0420` = `0.4.32`. **So the device reports firmware as `0.4.32` (no `_MMDD` suffix) and hardware in the same `x.y.z` form.**

### Device type IDs (`d4.r`) → app name (`c.a`)

| ID | Enum | App name |
|---|---|---|
| 0 | ALAYA_SILVER | Sabine ALAYA |
| 1 | ALAYA_PRO | Sabine ALAYA Pro |
| 16 | S_MIC | Sabine SMIC |
| 32 / 49 | K_MIC / K_SOLO | Sabine SOLO |
| 33 | SING_MIC | AudioWow S1 |
| 48 | K_HEADSET | — |
| 50 | MI_SOLO | Xiaomi K-song |
| 51 | XIAO_MAN | Edifier W800K |
| 52 | BZ_SET | ARENA |
| **64** | **M_MIKE** | **SmartMike+** |
| 65 | DIVINA | AudioWow / AudioWow B2 |
| 66 / 67 | M_MIKE_SLV / UAT_SILVER | SmartMikeSilver |
| 68 | HBM_SILVER | SmartMikeSilver or SmarterMike+ |
| 69 | M_MIKE_SLV_1 | Hohem SmartMic |
| 70 | M_MIKE_SLV_2 | Saramonic-BTW |
| 72 | M_MIKE_V2 | SmartMike Classic |
| 73 | M_MIKE_LOUD | OnyxGO |
| 80 | E_FINALACE | AudioWow B2 |

### STATUS_INFO (`o.o`) — TLV list from offset 17

| Type | Name | Value |
|---|---|---|
| 0 | STREAM_STATUS | |
| 1 | RSSI | |
| 2 | BAT_LEVEL | millivolts; app maps 3000→0 %, 3920→100 % linearly |
| 32 | A2DP_STATUS | START=128, STREAMING=129, END=130 |
| 48 | NTP_LOCAL_TIME | u32 device time |

### BUTTON_PRESSED (`o.d`) — event code at bytes 19–20 (`d4.g`)
POWER 0, RECORD 1, PLAY_PAUSE 2, VOL_UP 3, VOL_DOWN 4, FORWARD 5, BACKWARD 6, JACK_3PIN_INSERT 7, JACK_CTIA_INSERT 8, JACK_OMTP_INSERT 9, JACK_UNPLUGGED 10, EXT_KMIKE_PLUGGED 11, EXT_KMIKE_UNPLUGGED 12, BUTTON_PRESSED 13, BUTTON_RELEASE 14, MFB_SINGLE 15, MFB_DOUBLE 16, MFB_LONG 17, MFB_VLONG 18, MFB_VVLONG 19, EXT_LINEIN_PLUGGED 64, EXT_LINEIN_UNPLUGGED 65.

Jack events tell the app whether headphones are plugged in for monitoring.

## Public SDK names (SwissSample v1.4.14) → keys
`setMicVolume` → 5, `setMonitorVolume` → 11, `setAgcEnable` → 14, `setAns` → 15/34, `setReverberationRatio` → 16, `setDevocalMusicMixer` → 17, `setVoiceOfPeople`/devocal → 100, `setDspSigFlowBitmap` → 101, `startRecord`/`stopRecord` → 3/4.
