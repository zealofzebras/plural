# Authentication

**Status: largely resolved from code — device authentication appears NOT to be enforced by the app.**

## Two separate mechanisms

### 1. App SDK licence (`libjni-authenticate.so`) — not device related
- `com.sabinetek.swiss.sdk.SDKHelper`: native `init(context, appKey, appValue)` + `checkAuthenticate(handle)` verify the app's own SDK licence key.
- If the network is available, it also POSTs to `https://sdk.sabinetek.com/v1/auth/checkSdk` (`appKey`, `appValue`, `checkCode`).
- This gates the SDK inside the app; a third-party client talking to the mic directly does not need it.

### 2. Device serial-number challenge (protocol commands 32–34)
- The device sends `SN_AUTH_CHALLENGE` (32). Parser `o.m` requires a TLV length of 28 at offset 17, then reads:
  - bytes 21–36: serial number (16 bytes → hex string)
  - bytes 37–40, 41–42, 43–44: "machine code" (→ hex string)
- The app then POSTs `serialNumber` + `machineCode` to `https://sdk.sabinetek.com/v1/auth/checkSerialNumber` (`com.sabinetek.swiss.sdk.a.b`).
- **The callback is empty** (`p.a$a$a.C0577a.a()` does nothing), and nothing in 1.5.2 sends `SN_AUTH_RESPONSE` (key 27 is never called). So the app never answers the challenge.
- `SN_AUTH_REPORT` (34) carries the serial number; the app just stores it (`setSn`).
- `SN_AUTH_RESPONSE` format exists (`l5.e`): `00 14 00 00` + 16 bytes.

## Conclusion — Verified on hardware
On FW 0.4.37 the device sent only `SN_AUTH_REPORT`, never a challenge, and streamed audio without any authentication.

Observed `SN_AUTH_REPORT` payload: `00 1C 00 00` + **24 ASCII characters** (16-char serial + 8 more chars ending in `0000`). The app's parser treats them as hex bytes (21–36 = "serial", 37–44 = "machine code"), but the content is printable ASCII.
