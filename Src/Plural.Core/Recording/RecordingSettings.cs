namespace Plural.Core.Recording;

/// <summary>Output container. Every mic is always its own stream (track or file) so takes can be edited later.</summary>
public enum RecordingFormat
{
    /// <summary>One 16-bit PCM .wav per mic.</summary>
    Wav,
    /// <summary>One AAC .m4a per mic.</summary>
    M4a,
    /// <summary>One .mp3 per mic (LAME).</summary>
    Mp3,
    /// <summary>One Matroska audio file, one track per mic (FLAC or PCM).</summary>
    Mka,
    /// <summary>Matroska video: H.264 + one audio track per mic (FLAC or PCM).</summary>
    Mkv,
    /// <summary>MP4 video: H.264 + one AAC track per mic.</summary>
    Mp4,
}

/// <summary>Audio codec for mic tracks inside Matroska (MKA/MKV).</summary>
public enum MatroskaAudioCodec
{
    Flac,
    Pcm,
}

public static class RecordingFormats
{
    public static bool IsVideo(this RecordingFormat f) => f is RecordingFormat.Mkv or RecordingFormat.Mp4;

    /// <summary>True when all mics go into one file as separate tracks; false = one file per mic.</summary>
    public static bool IsMultiTrack(this RecordingFormat f) => f is RecordingFormat.Mka or RecordingFormat.Mkv or RecordingFormat.Mp4;

    public static string Extension(this RecordingFormat f) => f switch
    {
        RecordingFormat.Wav => ".wav",
        RecordingFormat.M4a => ".m4a",
        RecordingFormat.Mp3 => ".mp3",
        RecordingFormat.Mka => ".mka",
        RecordingFormat.Mkv => ".mkv",
        RecordingFormat.Mp4 => ".mp4",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    public static string DisplayName(this RecordingFormat f) => f switch
    {
        RecordingFormat.Wav => "WAV (one file per mic)",
        RecordingFormat.M4a => "M4A / AAC (one file per mic)",
        RecordingFormat.Mp3 => "MP3 (one file per mic)",
        RecordingFormat.Mka => "Matroska audio (one track per mic)",
        RecordingFormat.Mkv => "Matroska video (MKV)",
        RecordingFormat.Mp4 => "MP4 video",
        _ => f.ToString(),
    };

    public static IReadOnlyList<RecordingFormat> AudioFormats { get; } =
        [RecordingFormat.Wav, RecordingFormat.M4a, RecordingFormat.Mp3, RecordingFormat.Mka];

    public static IReadOnlyList<RecordingFormat> VideoFormats { get; } =
        [RecordingFormat.Mkv, RecordingFormat.Mp4];
}

/// <summary>Requested camera mode; the platform picks the closest supported one.</summary>
public sealed record VideoSettings
{
    public string? CameraId { get; init; }
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int FrameRate { get; init; } = 30;
    public int BitRate { get; init; } = 12_000_000;
}

/// <summary>User-configurable recording options (persisted by the app).</summary>
public sealed record RecordingSettings
{
    public RecordingFormat Format { get; init; } = RecordingFormat.Wav;
    public MatroskaAudioCodec MatroskaAudio { get; init; } = MatroskaAudioCodec.Flac;
    /// <summary>AAC bitrate per mic (M4A, MP4). Windows' AAC encoder supports 96/128/160/192 kbps, so offer those.</summary>
    public int AacBitRate { get; init; } = 192_000;
    /// <summary>MP3 bitrate per mic (CBR).</summary>
    public int Mp3BitRate { get; init; } = 320_000;
    public VideoSettings Video { get; init; } = new();
}
