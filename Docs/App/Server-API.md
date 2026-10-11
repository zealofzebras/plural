# Sabinetek backend API

## Hosts (`com/sabine/constants/a.java`)

| Constant | URL | Use |
|---|---|---|
| `f18750d` | `https://sapi.sabinetek.com` | API base for the `google` (international) channel |
| `f18751e` | `https://sapi.sabinetek.com.cn` | API base for the `domestic` (China) channel |
| `f18748b` | `https://api.sabinetek.com/` | Other API base |
| `f18749c` | `https://test2.sabinetek.com.cn` | Test server |
| `f18752f` | `http://192.168.3.166:8080/` | Internal dev server |
| — | `https://sdk.sabinetek.com/v1/auth/checkSdk`, `/v1/auth/checkSerialNumber`, `/v1/ali/getToken` | SDK licence / serial checks |

The base is chosen in `com/sabine/constants/MyApplication.java` `d()` by build channel; Retrofit client in `com/sabine/common/net/b.java`; endpoints in `m3/a.java`.

## Firmware update check

```
GET {base}/sapi/firmware/checkUpdate?type=1&packName=com.sabinetek.app&system=0&lang=en_US&appVersion=1.5.2&firmwareVersion=<FW>&hardwareVersion=<HW>
```

Built in `com/sabine/update/d.java` `a()`:

| Param | Value |
|---|---|
| `type` | Device type ID — SmartMike+ = `1` (see [README.md](README.md#supported-devices-from-comsabinecommondevicesa)) |
| `packName` | `com.sabinetek.app` |
| `system` | `0` (Android) |
| `lang` | `<language>_<COUNTRY>` from phone locale |
| `appVersion` | `1.5.2` |
| `firmwareVersion` | From device's `CUR_VERSION` report, formatted `major.minor.patch`, e.g. `0.4.32` (no `_MMDD` suffix) |
| `hardwareVersion` | Same `x.y.z` format from `CUR_VERSION`; `"0"` if not yet received. Actual value for SmartMike+ unknown. |

Response: `UpdateBean<FirmwareBean>` with `hasUpdate`, `firmwareVersion`, `file` (download URL, Inferred), `sign`, `info`, `appNeedUpdate`, `appVersion`.

## Other endpoints (`m3/a.java`)
`sapi/sign/login`, `sapi/sign/register`, `sapi/sign/createToken`, `sapi/device/binddevice`, `sapi/device/bindingdevices`, `sapi/device/deviceinfo`, `sapi/device/instruction`, `sapi/app/appUpdateNew`, `sapi/feature/getFeatureList`, `sapi/webresource/resourcelist`, member/verify/customer-service endpoints.

## Open questions
- Is the server still alive, and does `checkUpdate` work without login headers?
- Are images ≥ 0.4.33 (app references 0.4.38) still downloadable?
