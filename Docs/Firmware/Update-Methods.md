# Firmware update methods

## 1. PC updater (USB DFU) — `Firmware/Updater/`

`SmartMikeDFU20200702_1_1.exe` (SHA-256 `aad88b8dd62d168a9473fc3958a016194f0da7604b9735df95445186cb278959`) is an NSIS installer. Extracted to `Firmware/Updater/extracted/`:

| File | Purpose |
|---|---|
| `SmartMikeUpgrade.exe` | Updater GUI (built from `CSR_DFU\upgrade_wizard`, i.e. CSR's DFU wizard sample). Embeds the firmware image at offset 0x1f51e0. Contains the string `SmartMike+002`. |
| `DFUEngine.dll` | CSR DFU engine. Classes `DFUEngineUSB`, `DFUEngineCOM`, `DFUEngineSimulated`, `DFUFile`, `DFUFile2` → supports USB and COM-port transports. |
| `drivers/` | CSR drivers: `CSRBlueCoreUSB.inf` / `csrbc.sys` (BlueCore USB), `usbspi.sys` (CSR USB-SPI programmer), `parspi.sys` (parallel-port SPI) |
| `Readme.txt` | Firmware changelog (GBK-encoded Chinese + English) — summarized in [README.md](README.md) |
| `License.txt`, `vc_redist_x86_2013.exe`, `uninst.exe` | Installer support |

Inferred: the mic exposes a CSR USB DFU interface (VID 0x0A12, PID 0x1246) when put into DFU mode over its USB port. How to enter DFU mode is **Open** (changelog mentions "enter DFU mode" bug fixes).

## 2. Over-the-air via the app (SPP)

- SDK API: `FirmwareUpdater.getInstance().upFirmware(file, listener)` (SwissSample) — sends a `.dfu` file over the Bluetooth link.
- MetAudio downloads the file from the server (`FirmwareBean.file`, see [../App/Server-API.md](../App/Server-API.md)).
- App error strings: `OTA_CHECKSUM_ERROR`, `OTA_FLASH_CLAIM_ERROR`, `OTA_FLASH_INVALID_LEN`, `OTA_FLASH_OPEN_ERROR`, `OTA_FLASH_WRITE_ERROR`, `OTA_INVALID_PTR_ERROR`, `OTA_LENGTH_ERROR`, `OTA_LENGTH_OVERFLOW`, `OTA_UNKNOWN_ERROR`; targets `BLUETOOTH_OTA` and `CS_DSP_OTA`.
- Implementation: `p.b` (SDK `FirmwareUpdater`), payloads `l5.b`, ack parser `o.f`.

### OTA flow (`p.b`)
1. Read the first **20 bytes** of the update file as a header. The payload length is read as a little-endian u32 at offset 16 (`e4.e.g(reverse(hdr), 0, 3)`).
2. Send `DFU_BEGIN` (cmd 0) with that length.
3. Send the payload in **128-byte** `DFU_DATA` (cmd 1) frames; raw bytes are the frame payload. A running byte-sum is kept.
4. Send `DFU_END` (cmd 2) with the byte-sum.
5. The device acknowledges with `DFU_DATA` frames carrying a u32 at offset 17 (`o.f`). A timeout handler reports `TIME_OUT`.

DFU_BEGIN/END payload (`l5.b`, decompiled order is ambiguous): 8 bytes containing the TLV prefix `00 06`, the OTA target (`BLUETOOTH_OTA` = 0, `CS_DSP_OTA` = 1) and a u32 value. The exact layout needs checking against smali.

**Important:** the CSR `.dfu` files we have start with `CSR-dfu2` and have spaces at offset 16, so the app's OTA file format is **not** a raw `.dfu`. The server probably serves a Sabine wrapper (20-byte header + image). The SwissSample (SDK 1.4.14, 2018) passed a raw `.dfu` to an older updater. **Open.**

## 3. Hardware (SPI) — not yet investigated

CSR8670 has an SPI debug port usable with CSR USB-SPI programmers (BlueSuite) for full flash read/write. This is the recovery path if a modified image bricks a unit. **Open:** locate pads on the PCB.

## Reading firmware back
Neither USB DFU upload nor OTA has been tested for reading the image back. USB DFU defines an UPLOAD request; whether CSR's bootloader allows it is **Open**.
