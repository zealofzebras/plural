# Remote mode

One Plural app (the **controller**) starts and stops recording on others (**remotes**). Each remote records its own mics
(SmartMikes and local mics) and camera locally; when the take stops, it sends its files to the controller, which keeps one take
folder for everything. Every app can be either role; one app is never both at once.

Users see device names only. Wi‑Fi and Bluetooth are both searched and the best working path is used.

## User flow
- **Devices** (header) opens a sheet: **Search** lists nearby apps (any transport, merged by app id); **Control** adds one.
- The remote shows **Allow remote control?** with *Always allow this device*. Declined → the controller's card says so.
- Controller: a card per remote (state pill, mics live, level meter, Audio/Video mode, Connect mics, disconnect). **Record** starts
  this device (if a mic is live) and all connected remotes at one scheduled instant; **Stop** stops all, then a toast tracks
  "Collecting files from …" until every remote's files are in.
- Remote: a banner "Controlled by X" with Disconnect; its Record button belongs to the controller while connected.
- "Let other devices control this one" (Devices sheet, default on) makes the app findable and controllable.

## Take package
```
<root>/<yyyyMMdd_HHmmss>/
  take.json                 manifest (below)
  <controller's own files>  WAV/M4A/MP3 per mic, or one MKA/MKV/MP4
  <Remote name>/            one folder per remote (suffix " (2)" on name clashes)
    <its files>
```
Share/zip packs the whole folder (sub-folders kept) as `<take>.zip`. `take.json` lists, per device: role, folder, files,
`startOffsetMs` (its first sample vs. the scheduled start, controller clock), `clockUncertaintyMs` (half the best ping RTT),
seconds, status (`recording`, `syncing`, `synced`, `failed`) and errors. An editor lines tracks up by shifting each device by its offset.

## Protocol (`Plural.Core/Remote`)
Frames over any duplex stream: `kind u8 · length u32 LE · payload` (JSON, or raw bytes for FileData; max 1 MiB).

| Kind | Direction | Payload |
|---|---|---|
| Hello | both, first | protocol, id (per-install GUID), name, platform, hasVideo, wantsControl (false = discovery probe) |
| Accept / Reject | remote → controller | reason |
| Ping / Pong | controller → remote / back | T1 (controller clock), T2 (remote clock) |
| Status | remote → controller, 2 Hz | mics (name, live, peak), recording, takeId, seconds, video mode, camera ready, format |
| SetMode, ConnectMics | controller → remote | video |
| Start / Started | controller → remote / back | takeId, startAt **in the remote's clock** / actual t0 or error |
| Stop | controller → remote | takeId |
| TakeReady, FileBegin, FileData…, FileEnd | remote → controller | file list + remote t0; name, size; bytes; SHA‑256 |
| TakeAck | controller → remote | takeId (all files intact) |
| Pause / Resume, Paused (level 2) | controller → remote / back | takeId, `at` **in the remote's clock** (like Start; the controller uses now + 0.3 s so all devices pause together) / takeId, paused flag, applied instant or error |
| RecordRequest (level 2) | remote → controller | mic name. A mic button on the remote asks to toggle recording; the controller handles it like its own Record button |

- **Versions**: Hello's `protocol` is the wire format and stays **1** (release-1 apps refuse anything else); the feature level goes in
  a new `level` field (2 = Pause/Resume, RecordRequest, `Status.paused`), which release-1 apps ignore. A peer without `level` is
  level 1: it is accepted but flagged (`RemoteDevice.SupportsPause == false`, shown as "Doesn't support pause") and never sent
  Pause/Resume or RecordRequest. A higher level is accepted and used at our level; only a different wire `protocol` is refused
  ("different version").
- **Pause**: `RemoteDevice.PauseAsync/ResumeAsync(takeId, at)` → remote `IRemoteRecorder.PauseAsync/ResumeAsync` (app calls
  `MikeHub.PauseRecording(atMediaClock)`). Pauses are removed from the remote's files; the controller records them per device in
  `take.json` (`devices[].pauses[] = {startMs, endMs}`, wall-clock ms since the scheduled start on the controller clock; `endMs`
  null if the take ended paused), so the library can place markers in a remote track at `startMs − (startOffsetMs + earlier pauses)`.
- **Mic buttons**: the app on a controlled device calls `Responder.ForwardRecordButton()` when `MikeHub.RecordButtonPressed` fires;
  true = sent to the controller (do not record locally), false = handle as the local Record button. The controller raises
  `RemoteNetwork.RecordRequested` / `RemoteDevice.RecordRequested`.
- **Clock sync**: the controller pings (12 quick, then 1/s); offset = T2 − (T1 + T3)/2 from the lowest‑RTT of the last 30.
- **Start**: `startAt = now + 0.7 s` (1.2 s if any remote is on Bluetooth), converted per remote. Every recorder's `t0` is on its
  host `MediaClock`, and audio is clock-locked to it (`ClockLockedTrack`), so the residual offset in the manifest is what is left.
- **Robustness**: links close after 12 s of silence; the controller reconnects through any endpoint (backoff 2→10 s). A remote keeps
  recording if the controller disappears; a remote still recording a take the controller has ended gets Stop when it reconnects.
  Finished takes stay queued on the remote until TakeAck and are resent after a reconnect (a damaged file fails its hash and is resent).
  Received files are written as `.part` and renamed when complete.

## Transports
| | Wi‑Fi (`LanTransport`, Core) | Bluetooth (`BluetoothRemoteTransport` + `IBluetoothServicePlatform`) |
|---|---|---|
| Link | TCP 47830 (or any free port) | RFCOMM, service UUID `5f3a8c21-7d4e-4b9a-9c61-2e8d4f7a1b35` |
| Discovery | UDP 47831: `PLURAL?<id>` to 255.255.255.255, each subnet broadcast and 239.255.47.31; JSON reply | Each **paired** phone/computer (class of device) is probed with a Hello, two at a time, 10 s timeout |
| Windows | Firewall asks on first listen | Winsock `AF_BTH` server on any channel + `WSASetService` SDP record |
| Android | `INTERNET`, Wi‑Fi multicast lock while listening/searching | `listenUsingInsecureRfcommWithServiceRecord`; needs `BLUETOOTH_CONNECT` |

Wi‑Fi is preferred. Bluetooth file transfer is slow (roughly 1–2 Mbit/s), so a video take can take minutes to sync. Pair devices
in the OS Bluetooth settings first, as with SmartMikes; there is no in-app Bluetooth pairing.

## Testing
- `Plural.Core.Tests/RemoteTests.cs`: two networks over loopback (approve, decline, clock, start, files + hash, manifest, zip,
  resend after reconnect) and a real `LanTransport` discovery + TCP test.
- CLI: `smartmike serve --input Microphone --name X` (auto-allows), `smartmike control --record 3 --peer X`, `smartmike search`.

## Status (2026-10-12)
Tier 1 (2026-10-12): Hello `level` = 2, Pause/Resume, RecordRequest and the "Doesn't support pause" flag are built. Not yet run
between two devices.
Verified over Wi‑Fi between a Surface (arm64, CLI) and a Pixel 8 (Android 17, app):
- PC controls phone: found in ~100 ms, approval dialog, start +6…19 ms vs. schedule (±5 ms clock), 5 s WAV synced in 373 ms.
- Phone controls PC: Devices sheet search, remote card, Record/Stop from the phone, "Take complete · 2 devices"; package holds
  the phone's WAV, `Surface/…wav` and a correct `take.json` (phone +7 ms, PC +122 ms — the CLI test recorder's own start delay).
- Video: the PC asked the phone for video; it switched to Video, opened the camera and sent a 7.9 MB MKV (H.264 + FLAC) in 561 ms.
- Desktop app is findable over Wi‑Fi.
- Bluetooth only (phone Wi‑Fi off, PC and phone paired), both directions: the PC finds the phone in 0.5–2.4 s, the phone finds the
  PC through the Windows SDP service; audio and video takes start 12–25 ms after schedule (±4–6 ms clock); the phone as
  controller shows "Syncing · Receiving files… n%" and builds the package (its MKV + `Surface/…wav` + manifest).
- **Bluetooth throughput is ~55–80 KB/s** (0.5–0.6 Mbit/s): a 5 s WAV syncs in ~6 s, a 3 s 1080p MKV (~5 MB) in 66–89 s.
  Prefer Wi‑Fi for video; one-write framing and larger Windows socket buffers did not change it measurably.
- Clock samples taken during a sync are slow (pings wait behind file data), so the manifest uses the offset measured at Start
  and the estimator keeps the best of ~2 minutes of samples.
- A remote with no live mic now refuses Start at once ("Use Connect mics first") instead of connecting mics, which could take
  longer than the shared start (a SmartMike that is off times out after several seconds).

**Not yet tested**: the desktop app as remote/controller through its UI, two remotes at once, reconnect during a real take,
Pause/Resume and mic-button RecordRequest between two devices, a level-1 peer ("Doesn't support pause").
Open: Android 17 may need a local-network permission if the target SDK is raised. An Android remote that is being controlled runs in
the foreground service (`RecordingService`, untested on a phone); before that it had to stay open on screen.
