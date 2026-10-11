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
}

public interface IVideoPlatform
{
    IReadOnlyList<CameraInfo> GetCameras();

    /// <summary>Opens the camera; may prompt for permission first (the app head handles prompts before calling).</summary>
    Task<IVideoCapture> OpenAsync(CameraInfo camera, VideoSettings settings, CancellationToken ct = default);
}
