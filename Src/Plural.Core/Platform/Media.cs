using Plural.Core.Recording;

namespace Plural.Core.Platform;

// Platform media services used by the recorder. Containers (WAV, FLAC, Matroska, MP4) and MP3 (LAME)
// live in Core; only hardware/OS codecs and cameras are platform-specific.
//
// Timestamps everywhere are in 100 ns ticks on the shared host clock MediaClock (Stopwatch-based),
// so video frames and mic audio can be aligned.

/// <summary>Shared monotonic clock (100 ns ticks) for aligning audio and video.</summary>
public static class MediaClock
{
    private static readonly double TicksPerStopwatchTick = 10_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

    public static long Now => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * TicksPerStopwatchTick);

    /// <summary>Converts a Stopwatch timestamp to MediaClock ticks.</summary>
    public static long FromStopwatch(long stopwatchTimestamp) => (long)(stopwatchTimestamp * TicksPerStopwatchTick);
}

/// <summary>One encoded access unit. Video: H.264 Annex-B (start codes). AAC: raw frame (no ADTS).</summary>
public readonly record struct EncodedSample(ReadOnlyMemory<byte> Data, long Timestamp, bool IsKeyFrame);

/// <summary>AAC-LC encoder (platform: Android MediaCodec, Windows Media Foundation).</summary>
public interface IAacEncoder : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>AudioSpecificConfig (2 bytes for AAC-LC), for MP4 esds / Matroska CodecPrivate.</summary>
    ReadOnlyMemory<byte> AudioSpecificConfig { get; }

    /// <summary>
    /// Encoder delay in samples: how many samples at the start of the decoded stream are priming, not signal. The MP4/M4A edit list
    /// skips them, so a decoded file starts at the first real sample. Platform encoders report what they measured; the default
    /// is the AAC-LC frame size.
    /// </summary>
    int PrimingSamples => 1024;

    /// <summary>Feeds 16-bit PCM; encoded 1024-sample frames are returned through <paramref name="output"/> (synchronously, may be zero or more).</summary>
    void Encode(ReadOnlySpan<short> pcm, Action<EncodedSample> output);

    /// <summary>Drains remaining frames at end of stream.</summary>
    void Flush(Action<EncodedSample> output);
}

public interface IAacEncoderFactory
{
    IAacEncoder Create(int sampleRate, int channels, int bitRate);
}

public sealed record CameraInfo(string Id, string Name, bool IsFrontFacing);

/// <summary>Small BGRA32 frame for on-screen preview (not the recorded resolution).</summary>
public readonly record struct PreviewFrame(ReadOnlyMemory<byte> Bgra, int Width, int Height, int Stride);

/// <summary>Open camera: always previews; produces H.264 samples while encoding is on.</summary>
public interface IVideoCapture : IAsyncDisposable
{
    CameraInfo Camera { get; }

    /// <summary>Actual encoded size and frame rate.</summary>
    int Width { get; }
    int Height { get; }
    int FrameRate { get; }

    /// <summary>
    /// Clockwise rotation (0/90/180/270) needed to show frames upright, for the current device orientation.
    /// The preview applies it on screen; recorders store it as a display hint (MP4 tkhd matrix, Matroska ProjectionPoseRoll)
    /// using the value at the start of the take. Frames themselves are not rotated.
    /// </summary>
    int RotationDegrees => 0;

    /// <summary>
    /// Clockwise rotation the recorder should store as a display hint for the encoded frames. 0 when the platform
    /// already encodes upright frames (players handle hints inconsistently, especially in Matroska).
    /// </summary>
    int EncodedRotationDegrees => RotationDegrees;

    /// <summary>True when the preview should be mirrored (front cameras).</summary>
    bool MirrorPreview => false;

    /// <summary>Preview frames (~15 fps, ≤ 640 px wide), on a background thread.</summary>
    event Action<PreviewFrame>? PreviewFrameReady;

    /// <summary>H.264 Annex-B access units, on a background thread. The first sample after <see cref="StartEncoding"/> is a key frame
    /// that includes SPS/PPS.</summary>
    event Action<EncodedSample>? SampleEncoded;

    void StartEncoding();
    void StopEncoding();

    /// <summary>Asks the encoder to make the next frame a key frame (with SPS/PPS), e.g. when a paused take resumes. No-op if unsupported.</summary>
    void RequestKeyFrame() { }
}

public interface IVideoPlatform
{
    IReadOnlyList<CameraInfo> GetCameras();

    /// <summary>Opens the camera; may prompt for permission first (the app head handles prompts before calling).</summary>
    Task<IVideoCapture> OpenAsync(CameraInfo camera, VideoSettings settings, CancellationToken ct = default);
}

// ------------------------------------------------------------------ playback (decoders)
//
// Codec names used by the demuxers (Plural.Core.Media) and the decoder factories:
//   "pcm_s16le", "flac" (managed in Core), "aac" (AAC-LC, CodecPrivate = AudioSpecificConfig), "mp3", "h264" (CodecPrivate = avcC).

/// <summary>Audio decoder for a compressed codec (platform: Media Foundation, MediaCodec, AudioConverter).</summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>Output rate and channel count (interleaved 16-bit PCM).</summary>
    int SampleRate { get; }
    int Channels { get; }

    /// <summary>Decodes one packet (an AAC raw frame or an MP3 frame); decoded PCM is returned through <paramref name="output"/> (zero or more times, synchronously).</summary>
    void Decode(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<short>> output);

    /// <summary>Drains PCM still held by the decoder at end of stream.</summary>
    void Drain(Action<ReadOnlySpan<short>> output);

    /// <summary>Discards decoder state (after a seek); the next packet starts a new stream.</summary>
    void Reset();
}

public interface IAudioDecoderFactory
{
    /// <summary>True if this platform can decode <paramref name="codec"/> ("aac", "mp3").</summary>
    bool CanDecode(string codec);

    /// <summary>Creates a decoder. Throws <see cref="NotSupportedException"/> for codecs <see cref="CanDecode"/> rejects.</summary>
    IAudioDecoder Create(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate);
}

/// <summary>A decoded video frame: BGRA (not rotated; the UI applies the track's rotation) and its timestamp (100 ns ticks, file time).</summary>
public readonly record struct DecodedVideoFrame(PreviewFrame Frame, long Timestamp);

/// <summary>H.264 decoder (platform: Media Foundation, MediaCodec, VideoToolbox).</summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>
    /// Decodes one access unit (AVCC: length-prefixed NAL units, 4-byte lengths). Frames come out in presentation order through
    /// <paramref name="output"/>, possibly later than their input (decoder delay), scaled so the longer side ≤ the requested max size.
    /// </summary>
    void Decode(ReadOnlySpan<byte> accessUnit, long timestamp, bool keyFrame, Action<DecodedVideoFrame> output);

    /// <summary>Returns frames still held by the decoder (end of stream).</summary>
    void Drain(Action<DecodedVideoFrame> output);

    /// <summary>Discards queued frames and state (seek); the next access unit must be a key frame.</summary>
    void Flush();
}

public interface IVideoDecoderFactory
{
    /// <summary>True if this platform can decode <paramref name="codec"/> ("h264").</summary>
    bool CanDecode(string codec);

    /// <summary>
    /// Creates a decoder for a <paramref name="width"/>×<paramref name="height"/> stream; output frames are scaled down (aspect kept) so
    /// the longer side is at most <paramref name="maxSize"/> px.
    /// </summary>
    IVideoDecoder Create(string codec, ReadOnlyMemory<byte> codecPrivate, int width, int height, int maxSize);
}
