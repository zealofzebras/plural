namespace Plural.Core.Recording;

/// <summary>Rough size of a take per second of recorded time, and how long a given amount of free space lasts.</summary>
public static class RecordingEstimate
{
    /// <summary>Bytes per second of one 16-bit 44.1 kHz mono PCM track.</summary>
    public const double WavBytesPerSecond = 44100 * 2;

    /// <summary>FLAC (and so Matroska FLAC tracks) takes about this share of PCM on speech and music.</summary>
    public const double FlacRatio = 0.55;

    /// <summary>Free space always kept back when projecting (and at which a running take should stop).</summary>
    public const long DefaultReserveBytes = 200L * 1024 * 1024;

    /// <summary>The bitrate used when the app does not set one: by height and frame rate (matches the app's "Auto" setting).</summary>
    public static int AutoVideoBitRate(int height, int fps)
    {
        var baseRate = height >= 2160 ? 35_000_000 : height >= 1080 ? 12_000_000 : 6_000_000;
        var factor = fps >= 50 ? 1.5 : fps < 30 ? 0.8 : 1.0;
        return (int)(baseRate * factor);
    }

    /// <summary>
    /// Expected bytes per second of recorded time for <paramref name="audioTracks"/> mics in <c>settings.Format</c>; video formats add the
    /// camera's bitrate (<paramref name="video"/>, else <c>settings.Video</c>; a non-positive BitRate means the auto rule). Container overhead is ignored (&lt; 1 %).
    /// </summary>
    public static double BytesPerSecond(RecordingSettings settings, int audioTracks, VideoSettings? video)
    {
        var tracks = Math.Max(0, audioTracks);
        var perTrack = settings.Format switch
        {
            RecordingFormat.Wav => WavBytesPerSecond,
            RecordingFormat.M4a or RecordingFormat.Mp4 => settings.AacBitRate / 8.0,
            RecordingFormat.Mp3 => settings.Mp3BitRate / 8.0,
            _ => settings.MatroskaAudio == MatroskaAudioCodec.Flac ? WavBytesPerSecond * FlacRatio : WavBytesPerSecond,
        };
        var total = tracks * perTrack;
        if (settings.Format.IsVideo())
        {
            var v = video ?? settings.Video;
            var bits = v.BitRate > 0 ? v.BitRate : AutoVideoBitRate(v.Height, v.FrameRate);
            total += bits / 8.0;
        }
        return total;
    }

    /// <summary>Recording time that fits into <paramref name="freeBytes"/> after keeping <paramref name="reserveBytes"/> free; <see cref="TimeSpan.Zero"/> if nothing fits, <see cref="TimeSpan.MaxValue"/> if the rate is zero.</summary>
    public static TimeSpan TimeLeft(long freeBytes, double bytesPerSecond, long reserveBytes = DefaultReserveBytes)
    {
        if (bytesPerSecond <= 0) { return TimeSpan.MaxValue; }
        var usable = freeBytes - reserveBytes;
        if (usable <= 0) { return TimeSpan.Zero; }
        var seconds = usable / bytesPerSecond;
        return seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
    }
}
