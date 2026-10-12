using System.Buffers.Binary;
using System.Text;

namespace Plural.Core.Tests;

/// <summary>Independent minimal FLAC decoder (16-bit mono; constant / verbatim / fixed subframes) used to verify the encoder.</summary>
public static class TestFlacDecoder
{
    public sealed record Result(short[] Samples, int FrameCount, long TotalSamplesInfo, int SampleRate, int MaxBlock, int MinFrame, int MaxFrame);

    private sealed class Bits(byte[] d, int pos)
    {
        public int Pos = pos * 8; // bit position
        public int ByteAligned => Pos / 8;
        public uint Read(int n)
        {
            ulong v = 0;
            for (var i = 0; i < n; i++)
            {
                v = (v << 1) | (uint)((d[Pos >> 3] >> (7 - (Pos & 7))) & 1);
                Pos++;
            }
            return (uint)v;
        }
        public int ReadSigned(int n) => (int)(Read(n) << (32 - n)) >> (32 - n);
        public int Unary()
        {
            var q = 0;
            while (Read(1) == 0) { q++; }
            return q;
        }
    }

    private static int Crc8(byte[] d, int start, int end)
    {
        var c = 0;
        for (var i = start; i < end; i++)
        {
            c ^= d[i];
            for (var j = 0; j < 8; j++) { c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) & 0xFF : (c << 1) & 0xFF; }
        }
        return c;
    }

    private static int Crc16(byte[] d, int start, int end)
    {
        var c = 0;
        for (var i = start; i < end; i++)
        {
            c ^= d[i] << 8;
            for (var j = 0; j < 8; j++) { c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x8005) & 0xFFFF : (c << 1) & 0xFFFF; }
        }
        return c;
    }

    public static Result Decode(byte[] file)
    {
        Assert.Equal("fLaC", Encoding.ASCII.GetString(file, 0, 4));
        var p = 4;
        long total = 0; var rate = 0; var maxBlock = 0; var minFrame = 0; var maxFrame = 0;
        while (true)
        {
            var hdr = file[p];
            var len = (file[p + 1] << 16) | (file[p + 2] << 8) | file[p + 3];
            if ((hdr & 0x7F) == 0)
            {
                maxBlock = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(p + 4 + 2));
                minFrame = (file[p + 8] << 16) | (file[p + 9] << 8) | file[p + 10];
                maxFrame = (file[p + 11] << 16) | (file[p + 12] << 8) | file[p + 13];
                var v = BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(p + 4 + 10));
                rate = (int)(v >> 44);
                Assert.Equal(0UL, (v >> 41) & 7);
                Assert.Equal(15UL, (v >> 36) & 31);
                total = (long)(v & 0xF_FFFF_FFFFUL);
            }
            p += 4 + len;
            if ((hdr & 0x80) != 0) { break; }
        }

        var output = new List<short>();
        var frames = 0;
        while (p < file.Length)
        {
            var frameStart = p;
            Assert.Equal(0xFF, file[p]);
            Assert.Equal(0xF8, file[p + 1]);
            var bsCode = file[p + 2] >> 4;
            var srCode = file[p + 2] & 15;
            Assert.Equal(9, srCode);
            Assert.Equal(0, file[p + 3] >> 4);          // mono
            Assert.Equal(4, (file[p + 3] >> 1) & 7);    // 16-bit
            p += 4;
            // UTF-8 frame number
            var first = file[p++];
            long num;
            int extra;
            if (first < 0x80) { num = first; extra = 0; }
            else
            {
                extra = first >= 0xFC ? 5 : first >= 0xF8 ? 4 : first >= 0xF0 ? 3 : first >= 0xE0 ? 2 : 1;
                num = first & (0x3F >> extra);
                for (var i = 0; i < extra; i++)
                {
                    Assert.Equal(0x80, file[p] & 0xC0);
                    num = (num << 6) | (uint)(file[p++] & 0x3F);
                }
            }
            Assert.Equal(frames, num);
            var block = bsCode switch
            {
                12 => 4096,
                6 => file[p++] + 1,
                7 => (file[p++] << 8 | file[p++]) + 1,
                _ => throw new InvalidDataException($"block size code {bsCode}"),
            };
            Assert.Equal(Crc8(file, frameStart, p), file[p]);
            p++;

            var br = new Bits(file, p);
            Assert.Equal(0u, br.Read(1));
            var type = (int)br.Read(6);
            Assert.Equal(0u, br.Read(1));
            var x = new int[block];
            if (type == 0)
            {
                var c = br.ReadSigned(16);
                Array.Fill(x, c);
            }
            else if (type == 1)
            {
                for (var i = 0; i < block; i++) { x[i] = br.ReadSigned(16); }
            }
            else if (type is >= 8 and <= 12)
            {
                var order = type - 8;
                for (var i = 0; i < order; i++) { x[i] = br.ReadSigned(16); }
                Assert.Equal(0u, br.Read(2)); // 4-bit params
                var po = (int)br.Read(4);
                var parts = 1 << po;
                var idx = order;
                for (var part = 0; part < parts; part++)
                {
                    var k = (int)br.Read(4);
                    Assert.True(k < 15);
                    var cnt = (block >> po) - (part == 0 ? order : 0);
                    for (var i = 0; i < cnt; i++)
                    {
                        var q = br.Unary();
                        var u = (q << k) | (k > 0 ? (int)br.Read(k) : 0);
                        var r = (u >> 1) ^ -(u & 1);
                        x[idx] = r + order switch
                        {
                            0 => 0,
                            1 => x[idx - 1],
                            2 => 2 * x[idx - 1] - x[idx - 2],
                            3 => 3 * x[idx - 1] - 3 * x[idx - 2] + x[idx - 3],
                            _ => 4 * x[idx - 1] - 6 * x[idx - 2] + 4 * x[idx - 3] - x[idx - 4],
                        };
                        idx++;
                    }
                }
                Assert.Equal(block, idx);
            }
            else
            {
                throw new InvalidDataException($"subframe type {type}");
            }
            if ((br.Pos & 7) != 0) { Assert.Equal(0u, br.Read(8 - (br.Pos & 7))); }
            p = br.ByteAligned;
            Assert.Equal(Crc16(file, frameStart, p), BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(p)));
            p += 2;
            foreach (var v in x) { output.Add(checked((short)v)); }
            frames++;
        }
        return new Result(output.ToArray(), frames, total, rate, maxBlock, minFrame, maxFrame);
    }
}

/// <summary>Generic EBML tree parser for Matroska verification.</summary>
public sealed class EbmlNode
{
    public uint Id;
    public long DataStart, DataEnd;
    public bool UnknownSize;
    public byte[] File = [];
    public List<EbmlNode> Children = [];

    private static readonly HashSet<uint> Masters =
    [
        0x1A45DFA3, 0x18538067, 0x114D9B74, 0x4DBB, 0x1549A966, 0x1654AE6B, 0xAE, 0xE0, 0xE1, 0x7670,
        0x1F43B675, 0x1C53BB6B, 0xBB, 0xB7,
    ];

    public static EbmlNode ParseFile(byte[] file)
    {
        var root = new EbmlNode { File = file, DataStart = 0, DataEnd = file.Length };
        Parse(root, file);
        return root;
    }

    private static void Parse(EbmlNode parent, byte[] f)
    {
        var p = parent.DataStart;
        while (p < parent.DataEnd)
        {
            // id
            var b = f[p];
            var idLen = b >= 0x80 ? 1 : b >= 0x40 ? 2 : b >= 0x20 ? 3 : 4;
            uint id = 0;
            for (var i = 0; i < idLen; i++) { id = (id << 8) | f[p + i]; }
            p += idLen;
            b = f[p];
            var sLen = 1;
            while ((b & (0x80 >> (sLen - 1))) == 0) { sLen++; }
            var size = (long)(b & ((0x80 >> (sLen - 1)) - 1));
            var allOnes = size == (0x80 >> (sLen - 1)) - 1;
            for (var i = 1; i < sLen; i++) { size = (size << 8) | f[p + i]; allOnes &= f[p + i] == 0xFF; }
            p += sLen;
            var node = new EbmlNode { Id = id, File = f, DataStart = p, UnknownSize = allOnes };
            if (allOnes)
            {
                node.DataEnd = parent.DataEnd;
            }
            else
            {
                node.DataEnd = p + size;
                Assert.True(node.DataEnd <= parent.DataEnd, $"element {id:X} exceeds parent");
            }
            if (Masters.Contains(id))
            {
                Parse(node, f);
                if (allOnes)
                {
                    node.DataEnd = node.Children.Count == 0 ? p : node.Children[^1].DataEnd;
                }
            }
            parent.Children.Add(node);
            p = node.DataEnd;
            // an unknown-size cluster ends where the next level-1 element starts: handled by the cluster parse stopping at 1F43B675/1C53BB6B
            if (parent.Id == 0x1F43B675 && allOnes) { break; }
        }
    }

    public IEnumerable<EbmlNode> All(uint id) => Children.Where(c => c.Id == id);
    public EbmlNode? One(uint id) => Children.FirstOrDefault(c => c.Id == id);
    public ReadOnlySpan<byte> Data => File.AsSpan((int)DataStart, (int)(DataEnd - DataStart));
    public string Text => Encoding.UTF8.GetString(Data);

    public ulong UInt
    {
        get
        {
            ulong v = 0;
            foreach (var x in Data) { v = (v << 8) | x; }
            return v;
        }
    }

    public double Float => Data.Length == 4 ? BinaryPrimitives.ReadSingleBigEndian(Data) : BinaryPrimitives.ReadDoubleBigEndian(Data);
}

/// <summary>MP4 box tree.</summary>
public sealed class Mp4Box
{
    public string Type = "";
    public long Start, DataStart, End;
    public byte[] File = [];
    public List<Mp4Box> Children = [];

    private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl", "dinf", "udta", "edts", "mvex", "moof", "traf", "mfra"];

    public static Mp4Box ParseFile(byte[] f)
    {
        var root = new Mp4Box { File = f, DataStart = 0, End = f.Length };
        Parse(root, 0, f.Length);
        return root;
    }

    private static void Parse(Mp4Box parent, long from, long to)
    {
        var p = from;
        while (p + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(parent.File.AsSpan((int)p));
            var type = Encoding.ASCII.GetString(parent.File, (int)p + 4, 4);
            var header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(parent.File.AsSpan((int)p + 8));
                header = 16;
            }
            Assert.True(size >= header && p + size <= to, $"bad box {type} size {size} at {p}");
            var box = new Mp4Box { Type = type, Start = p, DataStart = p + header, End = p + size, File = parent.File };
            if (Containers.Contains(type)) { Parse(box, box.DataStart, box.End); }
            parent.Children.Add(box);
            p += size;
        }
        Assert.Equal(to, p);
    }

    public Mp4Box? Find(string type) => Children.FirstOrDefault(c => c.Type == type);
    public IEnumerable<Mp4Box> FindAll(string type) => Children.Where(c => c.Type == type);

    public Mp4Box Path(string path)
    {
        var b = this;
        foreach (var part in path.Split('/')) { b = b.Find(part) ?? throw new InvalidOperationException($"missing {part} in {path}"); }
        return b;
    }

    public ReadOnlySpan<byte> Data => File.AsSpan((int)DataStart, (int)(End - DataStart));
    public uint U32(int offsetInData) => BinaryPrimitives.ReadUInt32BigEndian(Data[offsetInData..]);
}

/// <summary>
/// Tests that measure wall-clock timing (take start/stop, latency, poster timing): run alone after the parallel tests, so CPU load
/// from fuzz/decoder tests can't push them out of their tolerances.
/// </summary>
[CollectionDefinition("timing", DisableParallelization = true)]
public sealed class TimingCollection;
