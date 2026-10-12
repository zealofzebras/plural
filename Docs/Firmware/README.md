# Firmware — overview

## Platform

| Item | Value | Status |
|---|---|---|
| Bluetooth SoC | CSR8670 (Qualcomm/CSR BlueCore, Kalimba DSP) | Inferred — SwissSample OTA files named `OTA_8670_*`, CSR DFU container, CSR USB VID 0x0A12 |
| Firmware base | CSR ADK "sink" (headset) application, customized by Sabine | Inferred — full HFP AT command set (`AT+BRSF`, `AT+CIND?`, `AT+CHLD`, `AT+CSRBATT`), voice-prompt files `0–9.prm/.idx/.txt` in image |
| Internal model name | `Sabine-S610` | Verified — UTF-16 string in SmartMike+ image @ 0xcbc0b |
| SPP service name | `M_MIKE` (clone reports `M_MIKE_SLV`) | Verified in image @ 0xcb7ad; clone value from Don Bowman's SDP dump |
| Second DSP | App lists `CS_DSP_OTA` alongside `BLUETOOTH_OTA` — possibly a separate DSP on newer models | Open |

## Image inventory (`SmartMike/Firmware/Images/`)

| File | Version | Size | USB PID | SHA-256 | Origin |
|---|---|---|---|---|---|
| `SmartMikePlus_0.4.32_0702.dfu` | 0.4.32_0702 | 869 776 | 0x1246 | `526dd536db34bd31d66289a3d729f1fbe8c1ad7720d16b41e5c64fe0f5940ce7` | Embedded in `SmartMikeUpgrade.exe` at offset 0x1f51e0 (from `SmartMikeDFU20200702_1_1.exe`) |
| `SwissSample/OTA_8670_0123.dfu` | "0123" | 840 256 | 0x1234 | `53c8c6c8dc537b739d0b83b80d2f303079b1dd8f0dffda0e6ac33e4daa7ee2c0` | SwissSample repo (2018), probably an earlier Sabine product |
| `SwissSample/OTA_8670_0123_OTATEST.dfu` | "0123" test | 840 264 | 0x1234 | `75e02fae78bd978cee8cd099d2112ffff9cf4669c7e50691943cd04ec190f4cb` | SwissSample repo |

All three have a valid USB DFU suffix CRC. Format details: [DFU-Format.md](DFU-Format.md).

## Known firmware versions (SmartMike+)

Version strings are `0.4.<minor>_<MMDD>`.

| Version | Date | Notes | Source |
|---|---|---|---|
| 0.4.26 | — | Protocol behaviour change in app | App code |
| 0.4.28_0110 | 2020-01-15 | Fix timestamp bug; fix long-time-work bug | Updater Readme |
| 0.4.29_0303 | 2020-03-03 | Optimized for TWS application; fix long-time lipsync bug | Updater Readme |
| 0.4.30_0408 | 2020-04-08 | Optimized for MS (mid-side) application; add AGC | Updater Readme |
| 0.4.30_0426 | 2020-04-26 | Deeper MS optimization; AGC for MS mode | Updater Readme |
| 0.4.31_0518 | 2020-05-18 / 05-28 | Add noise reduction; fix enter-DFU-mode bug | Updater Readme |
| 0.4.32_0618 | 2020-06-18 | Fix Android and PC connection problems | Updater Readme |
| **0.4.32_0702** | 2020-07-02 | **Image we have** | Embedded in updater |
| **0.4.37** | — | **Running on Peter's SmartMike+** (hardware 0.1.1, protocol 0.0.88) — no image available | `CUR_VERSION` from device, 2026-10-10 |
| 0.4.38 | — | Required by MetAudio for advanced settings — newer than anything we have | App code |

Hardware version reported by the device: **0.1.1**.

## Attempt to download newer firmware (2026-10-10) — none available
- `GET /sapi/firmware/checkUpdate?type=1&…&firmwareVersion=0.4.37&hardwareVersion=0.1.1` (and `0.4.32`):
  - `sapi.sabinetek.com` (35.82.33.235) and `api.sabinetek.com`: TCP timeout on 80/443 — server gone.
  - `sapi.sabinetek.com.cn`: connection refused.
  - `test2.sabinetek.com.cn`: HTTP 302 to Tencent DNSPod "web block" page.
  - `sdk.sabinetek.com`: HTTP redirects to HTTPS, which times out.
- Wayback Machine: no archived API responses or firmware URLs; only the two 2020 updaters.
- Mac updater (`SmartMike/Firmware/Updater/SmartMike_mac.dmg.zip`, extracted to `extracted_mac/`): Swift app "SmartMike+" by the same developer as SwissSample (`ztimc`). It has **no embedded image**; it downloads via a `BeeApi.checkFirmware` call to the (now dead) backend.
- Conclusion: the 0.4.37 on the device is newer than any image we can obtain. **Do not flash 0.4.32 over it** unless downgrading is intended; it would lose whatever 0.4.33–0.4.37 changed.

## Open questions
- Is the `fsr_dfu1` payload encrypted/signed? Bytes right after the header look high-entropy, but UTF-16 strings are readable further in.
- What files does the `fsr_dfu1` filesystem contain (VM app, Kalimba `.kap`, PS keys, prompts)?
- Where do newer images (0.4.33–0.4.38+) exist?
- What is the hardware recovery path (CSR SPI debug pads on the PCB)?
