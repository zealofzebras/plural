# Recording formats and video — design

Decided 2026-10-11 with Peter. Every mic is always its own stream (track or file) so takes can be edited later.

| Format | Output | Mic audio | Video |
|---|---|---|---|
| WAV | one `.wav` per mic | PCM 16-bit 44.1 kHz mono | — |
| M4A | one `.m4a` per mic (single-track MP4) | AAC-LC (platform encoder) | — |
| MP3 | one `.mp3` per mic | MP3 CBR (LAME, native lib like CELT) | — |
| MKA | one `.mka`, one track per mic | FLAC (default, managed encoder) or PCM | — |
| MKV | one `.mkv` | FLAC (default) or PCM, one track per mic | H.264 (platform encoder) |
| MP4 | one `.mp4` | AAC-LC, one track per mic | H.264 (platform encoder) |

- Per-mic file formats are stored as separate files in the take folder; **sharing/exporting** a take with several files sends one **zip** (`<take>.zip`).
- Track/file names = mic `DisplayName` (e.g. "SmartMike+ 0D5E"). Matroska/MP4 tracks carry the name and language `und`.
- Take folder: `<RecordingRoot>/<yyyyMMdd_HHmmss>/`; single-file formats write `<yyyyMMdd_HHmmss>.<ext>` inside it.

## Encoders: platform-native, containers managed
Contracts: `SmartMike.Core/Platform/Media.cs`, `SmartMike.Core/Recording/RecordingSettings.cs`.
- **Core (managed, all platforms):** WAV, FLAC encoder, Matroska writer, MP4/M4A writer, zip export, track sync.
- **Core + native lib:** MP3 via LAME 3.100 (`libmp3lame`), built per RID like CELT (`Native/lame/build-windows.sh`, `build-android.sh`).
- **Platform:** `IAacEncoderFactory` (Android MediaCodec `audio/mp4a-latm`; Windows Media Foundation AAC encoder MFT) and `IVideoPlatform` (Android Camera2 + MediaCodec `video/avc`; Windows Media Foundation source reader + H.264 encoder MFT).
- `MikeHub(bluetooth, audio, aac?, video?)`: formats needing a missing service are hidden in the UI.

## Timing and sync
- Common clock: `MediaClock.Now` (100 ns ticks, Stopwatch). Take starts at `t0` = the moment Record is pressed.
- Each mic's clock drifts ~0.2 % from the host. The recorder **locks every mic track to the host clock**: it tracks samples received vs. `MediaClock` elapsed and resamples (ratio clamped ±0.5 %, smoothed, like `JitterBuffer`) so a track has exactly `elapsed × 44100` samples. Tracks stay aligned with each other and with video over long takes.
- Mic tracks start at `t0` (first samples after Record). A mic that drops out gets silence for the gap, so later audio stays in place.
- Video samples carry `MediaClock` timestamps from the platform; the writer subtracts `t0`. The first video sample written must be a key frame (earlier ones are dropped).

## Implementation status (2026-10-11)
Built in `SmartMike.Core/Recording` (`FlacEncoder`, `MatroskaWriter`, `Mp4Writer`, `H264`, `LameMp3Encoder`, `ClockLockedTrack`, `TakeRecorder`, `RecordedTake`, `TakeExport`). Platform: `AndroidAacEncoder`, `AndroidVideoPlatform`/`AndroidVideoCapture` (Android); `MfAacEncoder`, `MfVideoPlatform`/`MfVideoCapture` (Windows).

| Part | State |
|---|---|
| Container writers, FLAC, LAME, clock-locked tracks | Built; unit-tested (46 tests pass on win-arm64 and win-x64): FLAC round-trip, Matroska/MP4 structure, LAME, sync drift/dropout, real capture → WAV/MKA bit-exact, fake end-to-end of all six formats |
| Rotation | `IVideoCapture.RotationDegrees` / `MirrorPreview` written as the MP4 `tkhd` matrix and the Matroska `ProjectionPoseRoll`. Sign not yet verified in players |
| Windows video | Verified on Surface arm64 + x64: 1080p30, 3 keyframes per 3 s, ~10.5 Mbps; QPC timestamps match `MediaClock`. Intel/AMD/NVIDIA hardware encoders untested |
| Android | AAC and camera code built. APK runs on a Pixel 8 (Android 17); the camera and the new formats are not yet run on the phone |
| Real mics + camera end-to-end | Not yet tested |

Open items:
- Audio is ~Bluetooth latency behind video. Measure with a clap test, then compensate.
- MP4/M4A need a seekable stream and write `moov` at the end, so a crash loses those takes. MKA/MKV survive a crash.
- No foreground service on Android: the screen must stay on while recording (the app keeps it on).
- Camera privacy-denied path (Windows and Android) untested.

## Containers
- **Matroska:** EBML header, Segment with unknown size, Info (TimestampScale 1 ms, MuxingApp/WritingApp "SmartMike Hub", Title), Tracks, then Clusters (unknown size, ≤ 1 s or 5 MB) of SimpleBlocks interleaved by timestamp; Cues and segment size patched on close if possible. Unknown sizes keep a crashed recording playable.
  - Video `V_MPEG4/ISO/AVC` + CodecPrivate avcC (from SPS/PPS); blocks in length-prefixed (AVCC) form.
  - Audio `A_FLAC` + CodecPrivate `fLaC`+STREAMINFO (block size 4096), or `A_PCM/INT/LIT` (16-bit), `A_AAC` + ASC.
- **MP4/M4A:** `ftyp` (isom/mp42; M4A brand `M4A `), `mdat` (64-bit size), `moov` written at the end: `mvhd`, one `trak` per stream (`tkhd`, `mdia`/`mdhd`/`hdlr`/`minf`/`stbl` with `stsd` avc1+avcC or mp4a+esds, `stts`, `stsz`, `stsc`, `co64`, `stss` for video), track names via `udta/name`. All audio tracks enabled.
- **FLAC encoder:** 16-bit mono, block size 4096, fixed predictors order 0–4 (pick smallest), partitioned Rice coding, verbatim fallback, CRC-8/CRC-16 per frame, STREAMINFO (MD5 may be zero).

## UI (shared)
- Modern look: dark-first studio style that follows system light/dark, accent colour, rounded cards, clear big Record button with timer, level meters with peak hold.
- **Mode:** Audio / Video toggle. Video shows the camera preview (from `IVideoCapture.PreviewFrameReady` → WriteableBitmap, shared code) and a camera picker.
- **Settings sheet:** format (filtered by mode and available services), Matroska codec (FLAC/PCM), AAC and MP3 bitrates, video resolution/fps/bitrate, recording folder (desktop). Settings persist (JSON in app data).
- Takes list: recent takes with format, duration, size; Open folder (desktop) / Share (Android, zip for multi-file).

## Permissions
- Android: `CAMERA` runtime permission before opening a camera (requested by AndroidHost); `BLUETOOTH_CONNECT` as before.
- Windows: camera privacy setting (Settings → Privacy → Camera → desktop apps); show a clear error if access is denied.
