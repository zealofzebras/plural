namespace Plural.Core.Media;

public enum MediaKind { Audio, Video }

/// <summary>
/// One track of a media file. <see cref="Index"/> is the position among the audio/video tracks in container order
/// (what the take sidecar stores as <c>trackIndex</c>). Codec names: <c>pcm_s16le</c>, <c>flac</c>, <c>aac</c>, <c>mp3</c>, <c>h264</c>
/// (other codecs keep a lower-case container name, e.g. <c>pcm_s24le</c>, <c>a_opus</c>, <c>mp4v</c>).
/// <see cref="CodecPrivate"/>: avcC for h264, AudioSpecificConfig for aac, "fLaC"+STREAMINFO for flac.
/// <see cref="Duration"/> is the track length where the container tells it cheaply, else the container duration.
/// </summary>
public sealed record MediaTrack(int Index, MediaKind Kind, string Codec, byte[]? CodecPrivate, string? Name,
    int SampleRate, int Channels, int BitsPerSample, int Width, int Height, int RotationDegrees, TimeSpan Duration);

/// <summary>
/// One compressed packet. <see cref="Timestamp"/> is in 100 ns ticks from the file start (presentation time, edit lists applied;
/// may be slightly negative for encoder priming packets).
/// </summary>
public readonly record struct MediaPacket(int Track, long Timestamp, ReadOnlyMemory<byte> Data, bool KeyFrame);

/// <summary>Sequential demuxer. Not thread-safe; one reader per consumer.</summary>
public interface IMediaReader : IDisposable
{
    IReadOnlyList<MediaTrack> Tracks { get; }
    TimeSpan Duration { get; }

    /// <summary>Restricts <see cref="TryRead"/> to these track indexes (the others are skipped without reading their payload). Null = all.</summary>
    void SelectTracks(IReadOnlyCollection<int>? tracks);

    /// <summary>Next packet in file order; false at the end (also at a truncated tail).</summary>
    bool TryRead(out MediaPacket packet);

    /// <summary>
    /// Positions the reader so that, for every selected track, the next packet is the last key frame (audio: packet) at or before
    /// <paramref name="position"/>; a track that has nothing that early starts at its first packet.
    /// </summary>
    void Seek(TimeSpan position);
}

public static class MediaReader
{
    /// <summary>Opens a file, sniffing the content (RIFF/WAVE, EBML, ISO-BMFF, MP3) rather than trusting the extension.</summary>
    public static IMediaReader Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        try { return Open(fs, leaveOpen: false); }
        catch { fs.Dispose(); throw; }
    }

    public static IMediaReader Open(Stream stream, bool leaveOpen = false)
    {
        Span<byte> h = stackalloc byte[12];
        stream.Position = 0;
        var n = stream.ReadAtLeast(h, 12, throwOnEndOfStream: false);
        stream.Position = 0;
        h = h[..n];
        if (n >= 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WAVE"u8)) { return new WavReader(stream, leaveOpen); }
        if (n >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3) { return new MatroskaReader(stream, leaveOpen); }
        if (n >= 8 && IsMp4Type(h[4..8])) { return new Mp4Reader(stream, leaveOpen); }
        if (n >= 3 && (h[..3].SequenceEqual("ID3"u8) || (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0))) { return new Mp3Reader(stream, leaveOpen); }
        throw new InvalidDataException("Unrecognized media file");
    }

    private static bool IsMp4Type(ReadOnlySpan<byte> t) =>
        t.SequenceEqual("ftyp"u8) || t.SequenceEqual("moov"u8) || t.SequenceEqual("mdat"u8) || t.SequenceEqual("free"u8)
        || t.SequenceEqual("wide"u8) || t.SequenceEqual("skip"u8) || t.SequenceEqual("styp"u8) || t.SequenceEqual("moof"u8);

    internal static bool Selected(IReadOnlyCollection<int>? sel, int track) => sel is null || sel.Contains(track);
}
