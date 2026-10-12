# CSR DFU file format (`CSR-dfu2`)

Observed on all three images in `SmartMike/Firmware/Images/`. Integers little-endian.

## Header

| Offset | Size | Value (SmartMike+ 0.4.32) | Meaning |
|---|---|---|---|
| 0x00 | 8 | `CSR-dfu2` | Magic |
| 0x08 | 2 | `3` | Format version (Verified: same in all images) |
| 0x0A | 4 | `869760` | Length of file **excluding** the 16-byte DFU suffix (Verified: `len + 16 == file size`) |
| 0x0E | 2 | `c8 04` | Unknown; identical in all images |
| 0x10 | 64 | spaces | Comment / description field (Inferred), blank in all images |
| 0x50 | … | zeros, then data | Unknown header area |
| 0x10a4 (SmartMike+), 0x700 (SwissSample) | — | `fsr_dfu1` | Start of an embedded CSR read-only filesystem image (Inferred) |

## USB DFU suffix (last 16 bytes) — standard USB DFU 1.0

| Field | SmartMike+ 0.4.32 | SwissSample 0123 |
|---|---|---|
| bcdDevice | 0xFFFF (any) | 0xFFFF |
| idProduct | 0x1246 | 0x1234 |
| idVendor | 0x0A12 (CSR) | 0x0A12 |
| bcdDFU | 0x0100 | 0x0100 |
| Signature | `UFD` | `UFD` |
| bLength | 16 | 16 |
| dwCRC | CRC-32 (no final XOR) over everything before the CRC | valid |

CRC check: `(~zlib.crc32(data[:-4])) & 0xffffffff == dwCRC`.

## `fsr_dfu1` payload

**Open.** Expected (CSR ADK convention) to contain the VM application, Kalimba DSP images (`.kap`), voice prompts (strings `0.prm`, `0.idx`, `0.txt` … `9.*` present), and possibly PS-key configuration. The 64 bytes after `fsr_dfu1` look high-entropy, which may mean a signature/hash block or encryption — needs analysis.

Readable UTF-16BE strings in the SmartMike+ image include `M_MIKE`, `SmartMike`, `SmartMike+`, `Sabine-S610`, `0.4.32_0702` and the HFP AT command set.
