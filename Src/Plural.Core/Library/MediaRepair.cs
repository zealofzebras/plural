using System.Buffers.Binary;
using Plural.Core.Media;

namespace Plural.Core.Library;

/// <summary>
/// Fixes the headers of recordings that were cut by a crash, used by <see cref="TakeLibrary.RecoverInterrupted"/>. The repairs are
/// deliberately tiny and in place: a few fixed-width header fields are overwritten after the file layout was verified, nothing is
/// copied, moved or appended. (A <c>.tmp</c> copy of a multi-GB recording would double the disk use right after a crash, when the disk
/// may be what ran out.) If the layout is not exactly what our writers produce, the file is left untouched.
/// <list type="bullet">
/// <item>WAV: data size 0 / 0xFFFFFFFF / larger than the file and the RIFF size are set to the real length; a trailing partial sample frame is cut.</item>
/// <item>Matroska (MKA/MKV): the unknown Segment size (8 bytes) and a zero Duration (float64 inside Info) are filled in. Unknown-size
/// clusters stay (legal, ended by the next cluster) and no Cues are added: players and <see cref="MatroskaReader"/> scan instead.
/// Building Cues would mean appending to a file whose tail may be cut mid-element.</item>
/// <item>Fragmented MP4 / M4A: nothing to do, every complete fragment is already playable and <see cref="Mp4Reader"/> takes the length from the fragments.</item>
/// </list>
/// </summary>
internal static class MediaRepair
{
    /// <summary>Repairs <paramref name="path"/> by extension; returns true if something was changed. Never throws for a bad file.</summary>
    public static bool TryRepair(string path)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".wav" => RepairWav(path),
                ".mka" or ".mkv" => RepairMatroska(path),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ WAV

    internal static bool RepairWav(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var len = fs.Length;
        Span<byte> h = stackalloc byte[12];
        if (fs.Read(h) < 12 || !h[..4].SequenceEqual("RIFF"u8) || !h[8..12].SequenceEqual("WAVE"u8)) { return false; }
        var blockAlign = 0;
        var tag = 0;
        Span<byte> ch = stackalloc byte[8];
        Span<byte> f = stackalloc byte[16];
        Span<byte> u = stackalloc byte[4];
        while (fs.Position + 8 <= len && fs.Read(ch) == 8)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(ch[4..]);
            var bodyPos = fs.Position;
            if (ch[..4].SequenceEqual("fmt "u8))
            {
                if (size < 16 || fs.Read(f) < 16) { return false; }
                tag = BinaryPrimitives.ReadUInt16LittleEndian(f);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(f[12..]);
            }
            else if (ch[..4].SequenceEqual("data"u8))
            {
                if (blockAlign <= 0 || tag is not (1 or 3 or 0xFFFE)) { return false; }
                var remaining = len - bodyPos;
                if (size != 0 && size != uint.MaxValue && size <= remaining) { return false; } // sizes are plausible: not a crash header
                var data = remaining - remaining % blockAlign;
                if (data > uint.MaxValue - 36) { return false; }
                if (data != remaining) { fs.SetLength(bodyPos + data); } // partial last frame
                var end = bodyPos + data;
                BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)data);
                fs.Position = bodyPos - 4;
                fs.Write(u);
                BinaryPrimitives.WriteUInt32LittleEndian(u, (uint)(end - 8));
                fs.Position = 4;
                fs.Write(u);
                fs.Flush();
                return true;
            }
            var next = bodyPos + size + (size & 1);
            if (next > len) { return false; }
            fs.Position = next;
        }
        return false;
    }

    // ------------------------------------------------------------------ Matroska

    private const uint IdEbml = 0x1A45DFA3, IdSegment = 0x18538067, IdInfo = 0x1549A966, IdCluster = 0x1F43B675;
    private const uint IdTimecodeScale = 0x2AD7B1, IdDuration = 0x4489;

    private static bool Header(byte[] d, int p, out uint id, out long size, out int hl)
    {
        id = 0; size = 0; hl = 0;
        if (p >= d.Length) { return false; }
        var il = 1;
        while (il <= 4 && (d[p] & (0x80 >> (il - 1))) == 0) { il++; }
        if (il > 4 || p + il >= d.Length) { return false; }
        for (var i = 0; i < il; i++) { id = id << 8 | d[p + i]; }
        var s = p + il;
        var sl = 1;
        while (sl <= 8 && (d[s] & (0x80 >> (sl - 1))) == 0) { sl++; }
        if (sl > 8 || s + sl > d.Length) { return false; }
        var v = (long)(d[s] & ((0x80 >> (sl - 1)) - 1));
        var ones = v == (0x80 >> (sl - 1)) - 1;
        for (var i = 1; i < sl; i++) { v = v << 8 | d[s + i]; ones &= d[s + i] == 0xFF; }
        size = ones ? -1 : v;
        hl = il + sl;
        return true;
    }

    internal static bool RepairMatroska(string path)
    {
        // Duration of the media as the reader derives it (scans clusters when Info has none); done before touching the file.
        TimeSpan duration;
        using (var reader = MediaReader.Open(path)) { duration = reader.Duration; }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var head = new byte[(int)Math.Min(fs.Length, 1 << 20)];
        fs.ReadExactly(head);
        if (!Header(head, 0, out var id, out var size, out var hl) || id != IdEbml || size < 0) { return false; }
        var p = hl + (int)size;
        if (!Header(head, p, out id, out size, out hl) || id != IdSegment) { return false; }
        var sizeFieldPos = p + (hl - 8);
        if (size >= 0 || hl < 8 + 4 || head[sizeFieldPos] != 0x01) { return false; } // only an 8-byte unknown size is ours to fill in
        var segStart = p + hl;

        long durationPos = -1;
        double scaleNs = 1_000_000;
        var q = segStart;
        while (Header(head, q, out id, out size, out hl) && id != IdCluster && size >= 0)
        {
            if (id == IdInfo)
            {
                var end = (int)Math.Min(head.Length, q + hl + size);
                for (var c = q + hl; c < end && Header(head, c, out var cid, out var csize, out var chl) && csize >= 0; c += chl + (int)csize)
                {
                    if (cid == IdTimecodeScale && csize is > 0 and <= 8)
                    {
                        ulong v = 0;
                        for (var i = 0; i < csize; i++) { v = v << 8 | head[c + chl + i]; }
                        scaleNs = v;
                    }
                    else if (cid == IdDuration && csize == 8 && c + chl + 8 <= head.Length)
                    {
                        if (BinaryPrimitives.ReadDoubleBigEndian(head.AsSpan(c + chl)) == 0) { durationPos = c + chl; }
                    }
                }
            }
            q += hl + (int)size;
            if (q >= head.Length) { break; }
        }

        var changed = false;
        if (durationPos >= 0 && duration > TimeSpan.Zero && scaleNs > 0)
        {
            var units = duration.Ticks * 100.0 / scaleNs;
            Span<byte> dbuf = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(dbuf, units);
            fs.Position = durationPos;
            fs.Write(dbuf);
            changed = true;
        }
        var segSize = fs.Length - segStart;
        if (segSize > 0 && segSize < (1L << 56) - 1)
        {
            Span<byte> sbuf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(sbuf, (ulong)segSize);
            sbuf[0] = 0x01;
            fs.Position = sizeFieldPos;
            fs.Write(sbuf);
            changed = true;
        }
        fs.Flush();
        return changed;
    }
}
