# Documentation index

Conventions used in all docs:
- **Verified** — seen directly in code, binaries or captures (cite file/offset).
- **Inferred** — reasoned from indirect evidence; say what the evidence is.
- **Open** — unknown; listed under "Open questions" in each doc.

## App (MetAudio Android app)
- [App/README.md](App/README.md) — overview: package, SDK, native libs, supported devices
- [App/Server-API.md](App/Server-API.md) — Sabinetek backend hosts and endpoints (incl. firmware update check)
- [App/Class-Map.md](App/Class-Map.md) — obfuscated class → meaning map for the jadx output

## Firmware
- [Firmware/README.md](Firmware/README.md) — chip, image inventory, versions and changelog
- [Firmware/DFU-Format.md](Firmware/DFU-Format.md) — CSR `CSR-dfu2` container and USB DFU suffix
- [Firmware/Update-Methods.md](Firmware/Update-Methods.md) — OTA via the app vs. PC USB updater

## Protocol (Bluetooth)
- [Protocol/README.md](Protocol/README.md) — profiles / SDP records, which channel carries what
- [Protocol/SPP-Framing.md](Protocol/SPP-Framing.md) — packet framing on the private SPP channel
- [Protocol/Commands.md](Protocol/Commands.md) — control commands (gain, monitor, AGC, ANS, …)
- [Protocol/Audio.md](Protocol/Audio.md) — audio stream format and codecs
- [Protocol/Authentication.md](Protocol/Authentication.md) — handshake (`libjni-authenticate`)

## Plural (our app)
- [Plural/Design.md](Plural/Design.md) — architecture, platform strategy, status

## Other
- [Sources.md](Sources.md) — external references
- [Plan.md](Plan.md) — work plan and status
- [../Tools/swiss_protocol.py](../Tools/swiss_protocol.py) — reference frame encoder/decoder
