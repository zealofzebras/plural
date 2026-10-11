namespace SmartMike.Core.Recording;

/// <summary>H.264 Annex-B helpers for the MP4 / Matroska muxers.</summary>
public static class H264
{
    public readonly record struct Nal(int Offset, int Length)
    {
        public byte Type(ReadOnlySpan<byte> data) => (byte)(data[Offset] & 0x1F);
    }

    /// <summary>Splits an Annex-B buffer (3- or 4-byte start codes) into NAL units (without start codes).</summary>
    public static List<Nal> SplitAnnexB(ReadOnlySpan<byte> data)
    {
        var list = new List<Nal>();
        var i = 0;
        var start = -1;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0)
                {
                    list.Add(Trim(data, start, i));
                }
                start = i + 3;
                i += 3;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0 && start < data.Length)
        {
            list.Add(Trim(data, start, data.Length));
        }
        return list;
    }

    private static Nal Trim(ReadOnlySpan<byte> data, int start, int end)
    {
        while (end > start && data[end - 1] == 0)
        {
            end--; // trailing zero bytes belong to the next (4-byte) start code
        }
        return new Nal(start, end - start);
    }

    /// <summary>Builds an avcC record from the SPS/PPS NAL units; null if the buffer has no SPS and PPS.</summary>
    public static byte[]? BuildAvcC(ReadOnlySpan<byte> annexB)
    {
        var sps = new List<byte[]>();
        var pps = new List<byte[]>();
        foreach (var n in SplitAnnexB(annexB))
        {
            if (n.Length < 1) { continue; }
            var t = n.Type(annexB);
            if (t == 7 && n.Length >= 4) { sps.Add(annexB.Slice(n.Offset, n.Length).ToArray()); }
            else if (t == 8) { pps.Add(annexB.Slice(n.Offset, n.Length).ToArray()); }
        }
        if (sps.Count == 0 || pps.Count == 0)
        {
            return null;
        }
        using var ms = new MemoryStream();
        ms.WriteByte(1);
        ms.WriteByte(sps[0][1]);
        ms.WriteByte(sps[0][2]);
        ms.WriteByte(sps[0][3]);
        ms.WriteByte(0xFF); // 4-byte NAL lengths
        ms.WriteByte((byte)(0xE0 | sps.Count));
        foreach (var s in sps) { ms.WriteByte((byte)(s.Length >> 8)); ms.WriteByte((byte)s.Length); ms.Write(s); }
        ms.WriteByte((byte)pps.Count);
        foreach (var p in pps) { ms.WriteByte((byte)(p.Length >> 8)); ms.WriteByte((byte)p.Length); ms.Write(p); }
        return ms.ToArray();
    }

    /// <summary>Converts Annex-B to 4-byte length-prefixed NAL units, dropping SPS (7), PPS (8) and AUD (9).</summary>
    public static byte[] ToLengthPrefixed(ReadOnlySpan<byte> annexB)
    {
        var nals = SplitAnnexB(annexB);
        var size = 0;
        foreach (var n in nals)
        {
            if (n.Length > 0 && n.Type(annexB) is not (7 or 8 or 9)) { size += 4 + n.Length; }
        }
        var outp = new byte[size];
        var o = 0;
        foreach (var n in nals)
        {
            if (n.Length == 0 || n.Type(annexB) is 7 or 8 or 9) { continue; }
            outp[o++] = (byte)(n.Length >> 24);
            outp[o++] = (byte)(n.Length >> 16);
            outp[o++] = (byte)(n.Length >> 8);
            outp[o++] = (byte)n.Length;
            annexB.Slice(n.Offset, n.Length).CopyTo(outp.AsSpan(o));
            o += n.Length;
        }
        return outp;
    }
}
