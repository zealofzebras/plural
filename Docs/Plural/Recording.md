# Recording formats and video — design

Decided 2026-10-11 with Peter. Every mic is always its own stream (track or file) so takes can be edited later.

| Format | Output | Mic audio | Video |
|---|---|---|---|
| WAV | one `.wav` per mic | PCM 16-bit 44.1 kHz mono | — |
| M4A | one `.m4a` per mic (single-track fragmented MP4) | AAC-LC (platform encoder) | — |
| MP3 | one `.mp3` per mic | MP3 CBR (LAME, native lib like CELT) | — |
| MKA | one `.mka`, one track per mic | FLAC (default, managed encoder) or PCM | — |
| MKV | one `.mkv` | FLAC (default) or PCM, one track per mic | H.264 (platform encoder) |
| MP4 | one `.mp4` (fragmented) | AAC-LC, one track per mic | H.264 (platform encoder) |

- Per-mic file formats are stored as separate files in the take folder; **sharing/exporting** a take with several files sends one **zip** (`<take>.zip`).
- Track/file names = mic `DisplayName` (e.g. "SmartMike+ 0D5E"). Matroska/MP4 tracks carry the name and language `und`.
- File names go through `Plural.Core.FileNames.Sanitize` (take, track and peak names, and files received from remotes): control characters and `<>:"/\|?*` removed, trailing dots and spaces dropped, Windows reserved names (CON, PRN, AUX, NUL, COM1–9, LPT1–9) handled, max 120 characters. Received duplicates become `name_2.ext`. Found on hardware: an Android mic named "Bluetooth: SmartMike+" became an NTFS alternate data stream on Windows.
- Take folder: `<RecordingRoot>/<yyyyMMdd_HHmmss>/`; single-file formats write `<yyyyMMdd_HHmmss>.<ext>` inside it.

## Take folder, sidecar and library (`Plural.Core/Library`)
- `plural.json` in every take folder (`TakeSidecar`), written at start (`recording`) and at stop (`complete`), atomic (`.tmp` + rename).
  Unknown fields are kept on rewrite; a corrupt file is kept as `plural.json.bak`.

| Field | Meaning |
|---|---|
| `version` | 1 |
| `title` | null = show the timestamp. Rename changes only this; the folder name is the id |
| `notes` | free text |
| `created` | local time the take started |
| `status` | `recording`, `complete`, `recovered` (`syncing` is shown on the Takes page for remote devices still sending) |
| `tracks[]` | `id` (`<file>#<trackIndex>`), `kind`, `file`, `trackIndex`, `name` (mic name), `device` (null = this device, else the remote's sub-folder), `startOffsetMs`, `durationMs`, `codec`, `sampleRate`, `channels`, `rotation`, `width`, `height` |
| `pauses[]` | `atMs`: pause position on the take's output timeline (ms, paused time removed) |
| `dropouts[]` | `track`, `startMs`, `endMs`: ms on that track's own timeline (sample 0, paused time removed) |
| `playback{}` | per track id: `gainDb`, `mute`, `solo` (player settings; all-default entries are dropped) |
| `deletedAt` | UTC; set while the take is in `.trash` |

- Units: all times in milliseconds. Remote tracks get `startOffsetMs` from the device's `take.json` entry, minus the controller's own
  offset, so the controller's tracks are at 0.
- **Takes without a sidecar** (recorded before this, or lost in a crash) are probed with the managed readers and get one sidecar.
  Remote folders are read through `take.json`.
- **Peaks**: `.peaks/<track id with / # \ replaced by _>.peaks`, little endian. Header 24 bytes: `PLPK`, version u16 (1), flags u16,
  sampleRate i32, blockSize u16 (256), decimation u16 (64), sampleCount i64. Level 0: one min/max `short` pair per 256 samples, appended
  while recording. Level 1: one pair per 64 level-0 blocks, written at the end. A crashed file (count 0) is read from its length.
  About 2.5 MB per track per hour. Takes without peaks (old, remote) get them from `PeakService` in the background, cached.
- **Poster**: video takes save `poster.png` from the preview frame about 1 s after start (`PosterCapture`).
- **Trash**: delete moves the folder to `<root>/.trash/` with `deletedAt` (the Takes page offers Undo). Restore moves it back.
  Trash older than 30 days is purged at app start. `.share/` holds titled share zips; `.`-folders are never scanned as takes.
- **Recovery on launch** (`TakeLibrary.RecoverInterrupted`): sidecars still saying `recording` become `recovered`; WAV and Matroska
  headers are repaired in place (`MediaRepair`) and durations are measured. The app's current take is skipped.

## Encoders: platform-native, containers managed
Contracts: `Plural.Core/Platform/Media.cs`, `Plural.Core/Recording/RecordingSettings.cs`.
- **Core (managed, all platforms):** WAV, FLAC encoder, Matroska writer, MP4/M4A writer, zip export, track sync.
- **Core + native lib:** MP3 via LAME 3.100 (`libmp3lame`), built per RID like CELT (`Native/lame/build-windows.sh`, `build-android.sh`).
- **Platform:** `IAacEncoderFactory` (Android MediaCodec `audio/mp4a-latm`; Windows Media Foundation AAC encoder MFT) and `IVideoPlatform` (Android Camera2 + MediaCodec `video/avc`; Windows Media Foundation source reader + H.264 encoder MFT).
- `MikeHub(bluetooth, audio, aac?, video?)`: formats needing a missing service are hidden in the UI.

## Timing and sync
- Common clock: `MediaClock.Now` (100 ns ticks, Stopwatch). Take starts at `t0` = the moment Record is pressed.
- Each mic's clock drifts ~0.2 % from the host. The recorder **locks every mic track to the host clock**: it tracks samples received vs. `MediaClock` elapsed and resamples (ratio clamped ±0.5 %, smoothed, like `JitterBuffer`) so a track has exactly `elapsed × 44100` samples. Tracks stay aligned with each other and with video over long takes.
- Mic tracks start at `t0` (first samples after Record). A mic that drops out gets silence for the gap, so later audio stays in place.
- Video samples carry `MediaClock` timestamps from the platform; the writer subtracts `t0`. The first video sample written must be a key frame (earlier ones are dropped).

## Implementation status (2026-10-13)
Built in `Plural.Core/Recording` (`FlacEncoder`, `MatroskaWriter`, `Mp4Writer` (fragmented), `H264`, `LameMp3Encoder`, `ClockLockedTrack`,
`PauseTimeline`, `RecordingEstimate`, `TakeRecorder`, `RecordedTake`, `TakeExport`) and `Plural.Core/Library` (sidecar, peaks, poster,
trash, recovery). Platform: `AndroidAacEncoder`, `AndroidVideoPlatform`/`AndroidVideoCapture` (Android); `MfAacEncoder`,
`MfVideoPlatform`/`MfVideoCapture` (Windows). Android background recording: `Plural.App.Android/RecordingService.cs`.

| Part | State |
|---|---|
| Container writers, FLAC, LAME, clock-locked tracks | Built; unit-tested: 350 Core tests pass on win-arm64 and win-x64 (timing-sensitive classes run in the non-parallel `timing` collection). Covers FLAC round-trip, Matroska/MP4 structure, LAME, sync drift/dropout, real capture → WAV/MKA bit-exact, fake end-to-end of all six formats |
| Fragmented MP4 / M4A | Built; unit-tested. Surface: MP4 force-killed at 8.0 s keeps 8.05 s video / 7.96 s audio and plays; files open and decode in Media Foundation. Pixel 8: force-stop → Recovered. Players **untested**: VLC, QuickTime, WMP/Photos, Android Photos, Resolve, Premiere |
| Pause / resume | Built (local, remote, CLI `pause`/`resume`); `PauseTests` cover the timeline. Verified 2026-10-13: Surface CLI, all six formats with pause record and decode; Pixel 8 MP4 and MKV with pause. Track alignment after resume not measured |
| Free space, batteries, reconnect | Built. Pixel 8: two ~350 ms SmartMike Bluetooth stalls logged as dropouts and filled with silence. A mic switched off mid-take is still to test |
| Rotation | `IVideoCapture.RotationDegrees` / `MirrorPreview` written as the MP4 `tkhd` matrix and the Matroska `ProjectionPoseRoll`. Sign not yet verified in players. Android: see the Android row |
| Windows video | Verified on Surface arm64 + x64: 1080p30, 3 keyframes per 3 s, ~10.5 Mbps; QPC timestamps match `MediaClock`. MF H.264 `RequestKeyFrame` verified with synthetic frames (QCOM hardware and Microsoft software MFT, arm64 + x64). Intel/AMD/NVIDIA hardware encoders untested |
| Android | AAC, camera, foreground service and decoders built. Pixel 8 (Android 17), 2026-10-13: 118 s WAV incl. 30 s screen-off; MP4 and MKV with pause; front and rear camera recorded upright (fixed Y-flip texture matrix, display rotation from `DisplayManager`, since `GetTransformMatrix` already holds the sensor rotation and the old double rotation turned video 90°); force-stop → Recovered; Takes and Player work. Lenovo Tab P10 (Android 9): AAC priming measured. **Not yet: Android 17 background video** |
| Real mics + camera end-to-end | Pixel 8 and Surface takes with pause, recovery and video verified (rows above). Not yet: SmartMike auto-reconnect with the mic switched off |

Open items:
- Audio is ~Bluetooth latency behind video. Measure with a clap test, then compensate.
- AAC priming: Android measured 2048 (see Priming); Apple still the 1024 default, unmeasured.
- Camera privacy-denied path (Windows and Android) untested.
- No "Finalize" rewrite of fMP4 to progressive MP4; add it only if an editor rejects the files.
- Android background video: allowed only if the service started while the app was visible; to check on Android 17.

## Pause / resume, dropouts, size estimate
- `MikeHub.PauseRecording()/ResumeRecording()` (+ overloads with a `MediaClock` instant, which may be in the near future) → `TakeRecorder.Pause/Resume` → `PauseTimeline` (host-clock spans; `Effective(t)` = recorded time). Paused time leaves every track: video frames inside a span are dropped and later timestamps are shifted by the paused time; on resume `IVideoCapture.RequestKeyFrame()` is called and delta frames are dropped until a key frame. Windows `MfH264Encoder` forces the IDR on the first input frame captured after the request, and `TakeRecorder` accepts a requested key frame captured up to 250 ms before the resume (`KeyFrameGrace`). Video gap after resume: 34 ms on Surface (was 1017 ms), 42 ms on Pixel 8 (was 953 ms). `RecordedTake.Duration` = recorded time. No-ops when not recording / already paused.
- `ClockLockedTrack`: output runs `Latency` behind host time, so a pause at host instant P is reached in the output `Latency` later (production time uses `Effective(now - lag)`). At output position nP = `Effective(P)` the track cuts its input at nP + the smoothed cursor offset (so the alignment before and after is the same), discards input worth (R − P) at the smoothed long-run input ratio (the mic's drift), and carries on with no silence and no controller windup. Accuracy is about 5 ms after the first seconds (the startup transient of the rate controller is not part of this).
- Sidecar: `pauses[].atMs` = position on the take's output timeline (recorded ms); `dropouts[]` = `{track, startMs, endMs}` in the track's own recorded ms (from its sample 0, pauses removed), written at the stop and every ~4 s. A dropout starts where the output stalled, which is up to the 60 ms jitter margin before the audio really ended.
- `RecordingEstimate.BytesPerSecond/TimeLeft` and `MikeHub.RecordingBytesWritten` feed the free-space warning (project from the real rate after ~10 s).
- **Free space**: `IAppHost.GetFreeSpace` (`DriveInfo`, `StatFs`). The settings sheet and the Record button show the time left before a take.
  While recording: a toast at 10 min left; at **200 MB** left (`RecordingEstimate.DefaultReserveBytes`) the take stops cleanly
  (files finalized) and the toast says why.
- **Batteries**: before a take, a SmartMike at or below 20 % is listed as a preflight note. During a take a toast names the mic at 15 %
  and again at 5 % (recording continues). Host battery under 15 % while not charging: one toast.
- **SmartMike reconnect**: a dropped link is retried (1, 2, 4, 10 s, then every 10 s) while a take runs; the gap is filled with silence
  and the sidecar gets a `dropouts[]` entry. Not in a take: 2 min. Pixel 8: two ~350 ms SmartMike Bluetooth stalls were logged as dropouts and filled with silence. A mic switched off mid-take is untested.

## Containers
- **Matroska:** EBML header, Segment with unknown size, Info (TimestampScale 1 ms, MuxingApp/WritingApp "Plural", Title), Tracks, then Clusters (unknown size, ≤ 1 s or 5 MB) of SimpleBlocks interleaved by timestamp; Cues and segment size patched on close if possible. Unknown sizes keep a crashed recording playable. Segment Duration ends where the last block ends (audio sinks pass the block's sample count, video one frame); it used to be the last block's start, ~70 ms short.
  - Video `V_MPEG4/ISO/AVC` + CodecPrivate avcC (from SPS/PPS); blocks in length-prefixed (AVCC) form.
  - Audio `A_FLAC` + CodecPrivate `fLaC`+STREAMINFO (block size 4096), or `A_PCM/INT/LIT` (16-bit), `A_AAC` + ASC.
- **MP4/M4A (fragmented):** `ftyp` (isom/mp42; M4A brand `M4A `), then `moov` with `mvex`/`mehd`/`trex` and empty sample tables
  (`mvhd`, one `trak` per stream: `tkhd`, `mdia`/`mdhd`/`hdlr`/`minf`/`stbl` with `stsd` avc1+avcC or mp4a+esds, track names via
  `udta/name`; all audio tracks enabled). Then one `moof`+`mdat` about every second (one `traf` per track with samples, `tfhd`
  default-base-is-moof, video fragments start on key frames) and an `mfra` index on a clean close.
  - `moov` waits until every track has its configuration (avcC and a first key frame; AAC AudioSpecificConfig). Fragments are held in
    memory meanwhile (up to 4 MB, then a standard AAC-LC config is synthesized). Audio waiting for video is flushed after 5 s.
  - Each fragment goes to the OS when written; `Flush(true)` every ~10 s.
  - **Edit lists** (video start offset, AAC priming) are a `free` box holding the `elst` while recording. A clean stop turns it into `edts`
    and patches the durations. A crashed file keeps the `free` box; `Mp4Reader` honours it.
  - **Crash behaviour**: everything up to the last complete fragment stays playable (no seeking needed). **Forced-kill test not yet run.**
  - **Priming**: `IAacEncoder.PrimingSamples`. Windows Media Foundation AAC = **0** (measured: decoder output aligned with the source; ffmpeg
    agrees). Android MediaCodec = **2048**, measured 2026-10-13 on Pixel 8 (Android 17) and Lenovo Tab P10 (Android 9) with the debug-only
    `AacSelfTest` (`adb shell am start -n net.getplural.app/crc64181cfaae50b6581c.MainActivity --ez selftest_aac true`, logcat tag `Plural`).
    It was 1024, which left audio 23 ms late. Apple AAC still the 1024 default (unmeasured).
  - A "Finalize" rewrite to progressive MP4 is **not built**; it is needed only if an editor rejects fMP4 (see Plan).
- **FLAC encoder:** 16-bit mono, block size 4096, fixed predictors order 0–4 (pick smallest), partitioned Rice coding, verbatim fallback, CRC-8/CRC-16 per frame, STREAMINFO (MD5 may be zero).

## UI (shared)
- Modern look: dark-first studio style that follows system light/dark, accent colour, rounded cards, clear big Record button with timer, level meters with peak hold.
- **Mode:** Audio / Video toggle. Video shows the camera preview (from `IVideoCapture.PreviewFrameReady` → WriteableBitmap, shared code) and a camera picker.
- **Settings sheet:** format (filtered by mode and available services), Matroska codec (FLAC/PCM), AAC and MP3 bitrates, video resolution/fps/bitrate, recording folder (desktop). Settings persist (JSON in app data).
- Takes list: recent takes with format, duration, size; Open folder (desktop) / Share (Android, zip for multi-file).

## Permissions
- Android: `CAMERA` runtime permission before opening a camera (requested by AndroidHost); `BLUETOOTH_CONNECT` as before.
- Windows: camera privacy setting (Settings → Privacy → Camera → desktop apps); show a clear error if access is denied.
