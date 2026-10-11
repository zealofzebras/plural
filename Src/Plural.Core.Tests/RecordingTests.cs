using System.Buffers.Binary;
using System.Text;
using SmartMike.Core.Audio;
using SmartMike.Core.Codec;
using SmartMike.Core.Platform;
using SmartMike.Core.Protocol;
using SmartMike.Core.Recording;
using SmartMike.Core.Session;

namespace SmartMike.Core.Tests;

public class FlacTests
{
    private static byte[] EncodeFile(short[] pcm, int chunk = 1000)
    {
        var ms = new MemoryStream();
        using (var w = new FlacFileWriter(ms, 44100, leaveOpen: true))
        {
            for (var i = 0; i < pcm.Length; i += chunk)
            {
                w.Write(pcm.AsSpan(i, Math.Min(chunk, pcm.Length - i)));
            }
        }
        return ms.ToArray();
    }

    private static short[] Signal(int n, int seed, double noise)
    {
        var rnd = new Random(seed);
        var x = new short[n];
        for (var i = 0; i < n; i++)
        {
            var v = 12000 * Math.Sin(i * 0.031) + 3000 * Math.Sin(i * 0.37) + noise * (rnd.NextDouble() - 0.5);
            x[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }
        return x;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(50_000)]
    public void RoundTrip_SineWithNoise(int n)
    {
        var pcm = Signal(n, 1, 200);
        var r = TestFlacDecoder.Decode(EncodeFile(pcm));
        Assert.Equal(pcm, r.Samples);
        Assert.Equal(n, r.TotalSamplesInfo);
        Assert.Equal(44100, r.SampleRate);
        Assert.Equal((n + 4095) / 4096, r.FrameCount);
    }

    [Fact]
    public void RoundTrip_WhiteNoise_UsesVerbatimAndStaysCorrect()
    {
        var rnd = new Random(7);
        var pcm = new short[20_000];
        for (var i = 0; i < pcm.Length; i++) { pcm[i] = (short)rnd.Next(short.MinValue, short.MaxValue + 1); }
        var file = EncodeFile(pcm);
        Assert.Equal(pcm, TestFlacDecoder.Decode(file).Samples);
        Assert.True(file.Length < pcm.Length * 2 + 400); // verbatim fallback bounds the size
    }

    [Fact]
    public void RoundTrip_ExtremesAndSilence()
    {
        var pcm = new short[12_000];
        for (var i = 0; i < 4096; i++) { pcm[i] = i % 2 == 0 ? short.MaxValue : short.MinValue; }
        // 4096..8191 silence (constant subframe), the rest constant non-zero
        Array.Fill(pcm, (short)-5, 8192, pcm.Length - 8192);
        var file = EncodeFile(pcm);
        Assert.Equal(pcm, TestFlacDecoder.Decode(file).Samples);
    }

    [Fact]
    public void CompressesTonalSignal()
    {
        var pcm = Signal(44100, 3, 20);
        var file = EncodeFile(pcm);
        Assert.True(file.Length < pcm.Length * 2 * 0.6, $"{file.Length} bytes");
    }

    [Fact]
    public void FrameNumbers_UseMultiByteUtf8()
    {
        // 2100 silent blocks -> frame numbers beyond 0x7FF (3-byte UTF-8 form); constant subframes keep it cheap.
        var pcm = new short[4096 * 2100 + 5];
        var r = TestFlacDecoder.Decode(EncodeFile(pcm, 65536));
        Assert.Equal(2101, r.FrameCount);
        Assert.Equal(pcm.Length, r.Samples.Length);
    }

    [Fact]
    public void StreamInfo_HasSizesAndCodecPrivateLayout()
    {
        var pcm = Signal(10_000, 5, 100);
        var enc = new FlacEncoder();
        var priv = enc.BuildHeader();
        Assert.Equal("fLaC", Encoding.ASCII.GetString(priv, 0, 4));
        Assert.Equal(0x80, priv[4]);
        Assert.Equal(34, priv[7]);
        var frames = new List<int>();
        enc.Encode(pcm, (f, n) => frames.Add(f.Length));
        enc.Flush((f, n) => frames.Add(f.Length));
        Assert.Equal(3, frames.Count);
        Assert.Equal(frames.Min(), enc.MinFrameSize);
        Assert.Equal(frames.Max(), enc.MaxFrameSize);
        Assert.Equal(10_000, enc.TotalSamples);
        Assert.Equal(4096, enc.MaxBlockSize);
    }
}

public class MatroskaTests
{
    private static byte[] AnnexBKeyFrame()
    {
        byte[] sps = [0x67, 0x42, 0xC0, 0x1F, 0xDA, 0x01, 0x40, 0x16];
        byte[] pps = [0x68, 0xCE, 0x3C, 0x80];
        byte[] idr = [0x65, 0x88, 0x84, 0x00, 0x10, 0xFF];
        return [0, 0, 0, 1, .. sps, 0, 0, 0, 1, .. pps, 0, 0, 1, .. idr];
    }

    [Fact]
    public void H264_AvcCAndLengthPrefix()
    {
        var f = AnnexBKeyFrame();
        var avcC = H264.BuildAvcC(f)!;
        Assert.Equal(1, avcC[0]);
        Assert.Equal(0x42, avcC[1]);
        Assert.Equal(0x1F, avcC[3]);
        Assert.Equal(0xFF, avcC[4]);
        Assert.Equal(0xE1, avcC[5]);
        Assert.Equal(8, avcC[7]);
        var lp = H264.ToLengthPrefixed(f);
        Assert.Equal([0, 0, 0, 6, 0x65, 0x88, 0x84, 0x00, 0x10, 0xFF], lp);
        Assert.Null(H264.BuildAvcC([0, 0, 1, 0x65, 1, 2]));
    }

    [Fact]
    public void Structure_VideoAndTwoFlacTracks()
    {
        var ms = new MemoryStream();
        var key = AnnexBKeyFrame();
        int videoBlocks = 0, audioBlocks = 0;
        using (var w = new MatroskaWriter(ms, "Test take", leaveOpen: true))
        {
            var v = w.AddVideo("Video", 1280, 720, 30, rotationDegrees: 90);
            var flacs = new[] { new FlacEncoder(), new FlacEncoder() };
            var a = new[]
            {
                w.AddAudio("SmartMike+ 0D5E", "A_FLAC", 44100, 1, 16, flacs[0].BuildHeader()),
                w.AddAudio("SmartMike+ A1B2", "A_FLAC", 44100, 1, 16, flacs[1].BuildHeader()),
            };
            // chronological feed; the avcC arrives only after the first few audio blocks (header is held back)
            var pcm = new short[4096 * 30];
            for (var i = 0; i < pcm.Length; i++) { pcm[i] = (short)(8000 * Math.Sin(i * 0.05)); }
            var items = new List<(long Ts, int Order, Action Write)>();
            for (var m = 0; m < 2; m++)
            {
                var mm = m;
                long samples = 0;
                flacs[m].Encode(pcm, (f, n) =>
                {
                    var data = f.ToArray();
                    var ts = samples * 10_000_000 / 44100;
                    items.Add((ts, 1, () => w.WriteBlock(a[mm], ts, data)));
                    samples += n;
                    audioBlocks++;
                });
            }
            for (var i = 0; i < 80; i++)
            {
                var ts = i * 10_000_000L / 30;
                var k = i;
                items.Add((ts, 0, () => w.WriteBlock(v, ts, H264.ToLengthPrefixed(key), keyFrame: k % 30 == 0)));
                videoBlocks++;
            }
            var count = 0;
            foreach (var it in items.OrderBy(x => x.Ts).ThenBy(x => x.Order))
            {
                if (count++ == 3) { w.SetCodecPrivate(v, H264.BuildAvcC(key)!); }
                it.Write();
            }
        }
        var file = ms.ToArray();
        var root = EbmlNode.ParseFile(file);
        Assert.Equal("matroska", root.One(0x1A45DFA3)!.One(0x4282)!.Text);
        var seg = root.One(0x18538067)!;
        Assert.False(seg.UnknownSize);
        Assert.Equal(file.Length, seg.DataEnd);

        var info = seg.One(0x1549A966)!;
        Assert.Equal("Test take", info.One(0x7BA9)!.Text);
        Assert.Equal("SmartMike Hub", info.One(0x5741)!.Text);
        Assert.Equal(1_000_000UL, info.One(0x2AD7B1)!.UInt);
        Assert.True(info.One(0x4489)!.Float > 2500);

        var tracks = seg.One(0x1654AE6B)!.All(0xAE).ToList();
        Assert.Equal(3, tracks.Count);
        Assert.Equal(["V_MPEG4/ISO/AVC", "A_FLAC", "A_FLAC"], tracks.Select(t => t.One(0x86)!.Text));
        Assert.Equal(["Video", "SmartMike+ 0D5E", "SmartMike+ A1B2"], tracks.Select(t => t.One(0x536E)!.Text));
        Assert.All(tracks, t => Assert.Equal("und", t.One(0x22B59C)!.Text));
        Assert.Equal(1, tracks[0].One(0x63A2)!.Data[0]); // avcC
        Assert.Equal("fLaC", Encoding.ASCII.GetString(tracks[1].One(0x63A2)!.Data[..4]));
        var video = tracks[0].One(0xE0)!;
        Assert.Equal(1280UL, video.One(0xB0)!.UInt);
        Assert.Equal(90.0, video.One(0x7670)!.One(0x7675)!.Float);
        Assert.Equal(44100.0, tracks[1].One(0xE1)!.One(0xB5)!.Float);

        var clusters = seg.All(0x1F43B675).ToList();
        Assert.True(clusters.Count >= 2);
        var perTrack = new Dictionary<int, int>();
        long lastCluster = -1;
        foreach (var c in clusters)
        {
            Assert.False(c.UnknownSize);
            var ts = (long)c.One(0xE7)!.UInt;
            Assert.True(ts > lastCluster);
            lastCluster = ts;
            long last = -1;
            foreach (var b in c.All(0xA3))
            {
                var d = b.Data;
                var track = d[0] & 0x7F;
                var rel = BinaryPrimitives.ReadInt16BigEndian(d[1..]);
                Assert.InRange(rel, -10, 1000);
                var abs = ts + rel;
                Assert.True(abs >= last - 100, "blocks roughly ordered");
                last = Math.Max(last, abs);
                perTrack[track] = perTrack.GetValueOrDefault(track) + 1;
            }
        }
        Assert.Equal(videoBlocks, perTrack[1]);
        Assert.Equal(audioBlocks / 2, perTrack[2]);
        Assert.Equal(audioBlocks / 2, perTrack[3]);

        var cues = seg.One(0x1C53BB6B)!.All(0xBB).ToList();
        Assert.True(cues.Count >= clusters.Count);
        // first cue points at the first cluster (position relative to segment data)
        var pos = (long)cues[0].One(0xB7)!.One(0xF1)!.UInt;
        Assert.Equal(clusters[0].DataStart - 12, seg.DataStart + pos);
        // seek head points at Cues
        var seekPos = seg.One(0x114D9B74)!.All(0x4DBB).Select(s => (long)s.One(0x53AC)!.UInt).Last();
        Assert.Equal(file.AsSpan().LastIndexOf(new byte[] { 0x1C, 0x53, 0xBB, 0x6B }) - seg.DataStart, seekPos);
    }

    [Fact]
    public void NonSeekableStream_StaysUnknownSizeAndDoesNotThrow()
    {
        var inner = new MemoryStream();
        using (var w = new MatroskaWriter(new NonSeekable(inner), "t", leaveOpen: true))
        {
            var t = w.AddAudio("a", "A_PCM/INT/LIT", 44100, 1, 16);
            for (var i = 0; i < 50; i++) { w.WriteBlock(t, i * 1_000_000L, new byte[100]); }
        }
        var b = inner.ToArray();
        // Segment id followed by 8-byte unknown size
        var idx = b.AsSpan().IndexOf(new byte[] { 0x18, 0x53, 0x80, 0x67 });
        Assert.True(idx > 0);
        Assert.Equal(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, b[(idx + 4)..(idx + 12)]);
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

public class Mp4Tests
{
    [Fact]
    public void VideoPlusTwoAacTracks_BoxTree()
    {
        var ms = new MemoryStream();
        byte[] avcC = [1, 0x42, 0xC0, 0x1F, 0xFF, 0xE1, 0, 2, 0x67, 0x42, 1, 0, 1, 0x68];
        byte[] asc = [0x12, 0x08];
        var videoSamples = new List<byte[]>();
        using (var w = new Mp4Writer(ms, audioOnly: false, leaveOpen: true))
        {
            var v = w.AddVideo("Video", 1920, 1080, rotationDegrees: 90);
            v.AvcC = avcC;
            var a1 = w.AddAudio("Mic One", 44100, 1, asc);
            var a2 = w.AddAudio("Mic Two", 44100, 1, asc);
            for (var i = 0; i < 90; i++)
            {
                var s = Enumerable.Repeat((byte)i, 50 + i).ToArray();
                videoSamples.Add(s);
                w.WriteSample(v, 200_000_000 / 10 + i * 10_000_000L / 30, s, i % 30 == 0);
                for (var k = 0; k < 4; k++)
                {
                    w.WriteSample(a1, 0, new byte[] { 1, 2, 3, (byte)k });
                    w.WriteSample(a2, 0, new byte[] { 4, 5, 6, (byte)k, 7 });
                }
            }
        }
        var file = ms.ToArray();
        var root = Mp4Box.ParseFile(file);
        Assert.Equal(["ftyp", "mdat", "moov"], root.Children.Select(c => c.Type));
        Assert.Equal("isom", Encoding.ASCII.GetString(root.Children[0].Data[..4]));
        // 64-bit mdat: size == 1 then largesize
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)root.Children[1].Start)));
        Assert.Equal(root.Children[1].End - root.Children[1].Start, (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan((int)root.Children[1].Start + 8)));

        var moov = root.Children[2];
        var traks = moov.FindAll("trak").ToList();
        Assert.Equal(3, traks.Count);
        Assert.Equal(["Video", "Mic One", "Mic Two"], traks.Select(t => Encoding.UTF8.GetString(t.Path("udta/name").Data)));
        Assert.Equal(["vide", "soun", "soun"], traks.Select(t => Encoding.ASCII.GetString(t.Path("mdia/hdlr").Data.Slice(8, 4))));
        Assert.Equal(["Video", "Mic One", "Mic Two"],
            traks.Select(t => { var d = t.Path("mdia/hdlr").Data; return Encoding.UTF8.GetString(d.Slice(24, d.Length - 25)); }));

        int[] expectedCounts = [90, 360, 360];
        for (var i = 0; i < 3; i++)
        {
            var stbl = traks[i].Path("mdia/minf/stbl");
            Assert.Equal((uint)expectedCounts[i], stbl.Path("stsz").U32(8));
            var co = stbl.Path("co64");
            Assert.True(co.U32(4) >= 1);
            Assert.NotNull(stbl.Find("stsc"));
            Assert.Equal(i == 0, stbl.Find("stss") is not null);
        }
        // key frames 1, 31, 61
        var stss = traks[0].Path("mdia/minf/stbl/stss");
        Assert.Equal(3u, stss.U32(4));
        Assert.Equal([1u, 31u, 61u], new[] { stss.U32(8), stss.U32(12), stss.U32(16) });
        // video timescale and audio timescale
        Assert.Equal(90000u, traks[0].Path("mdia/mdhd").U32(12));
        Assert.Equal(44100u, traks[1].Path("mdia/mdhd").U32(12));
        // stts: constant 3000 ticks per frame
        var stts = traks[0].Path("mdia/minf/stbl/stts");
        Assert.Equal(1u, stts.U32(4));
        Assert.Equal(90u, stts.U32(8));
        Assert.Equal(3000u, stts.U32(12));
        // first video chunk contents are in mdat
        var off = (long)BinaryPrimitives.ReadUInt64BigEndian(traks[0].Path("mdia/minf/stbl/co64").Data[8..]);
        Assert.Equal(videoSamples[0], file[(int)off..((int)off + videoSamples[0].Length)]);
        // avc1/avcC and esds
        var stsdV = traks[0].Path("mdia/minf/stbl/stsd");
        Assert.Contains("avc1", Encoding.ASCII.GetString(stsdV.Data));
        Assert.True(stsdV.Data.IndexOf(avcC) > 0);
        var stsdA = traks[1].Path("mdia/minf/stbl/stsd").Data;
        Assert.True(stsdA.IndexOf("esds"u8) > 0);
        Assert.True(stsdA.IndexOf(new byte[] { 0x05, 0x02, 0x12, 0x08 }) > 0);
        // late first video frame (20 s) becomes an empty edit
        Assert.NotNull(traks[0].Find("edts"));
        Assert.Null(traks[1].Find("edts"));
    }

    [Fact]
    public void Rotation90_WritesStandardMatrix()
    {
        var ms = new MemoryStream();
        using (var w = new Mp4Writer(ms, false, leaveOpen: true))
        {
            var v = w.AddVideo("V", 640, 480, rotationDegrees: 90);
            v.AvcC = [1, 2, 3, 4, 0xFF, 0xE0, 0];
            w.WriteSample(v, 0, [1, 2, 3], true);
        }
        var moov = Mp4Box.ParseFile(ms.ToArray()).Find("moov")!;
        var tkhd = moov.Path("trak/tkhd").Data.ToArray();
        // v0 tkhd: 4 ver/flags + 4+4 times + 4 id + 4 res + 4 dur + 8 res + 2+2+2+2 = 36 -> matrix at 40
        var m = Enumerable.Range(0, 9).Select(i => BinaryPrimitives.ReadInt32BigEndian(tkhd.AsSpan(40 + 4 * i))).ToArray();
        Assert.Equal([0, 0x10000, 0, -0x10000, 0, 0, 0, 0, 0x40000000], m);
        // width / height stay unrotated (16.16)
        Assert.Equal(640u << 16, BinaryPrimitives.ReadUInt32BigEndian(tkhd.AsSpan(76)));
        Assert.Equal(480u << 16, BinaryPrimitives.ReadUInt32BigEndian(tkhd.AsSpan(80)));
    }

    [Fact]
    public void M4aBrandAndSingleTrack()
    {
        var ms = new MemoryStream();
        using (var w = new Mp4Writer(ms, audioOnly: true, leaveOpen: true))
        {
            var a = w.AddAudio("SmartMike+ 0D5E", 44100, 1, [0x12, 0x08]);
            for (var i = 0; i < 100; i++) { w.WriteSample(a, 0, new byte[200]); }
        }
        var root = Mp4Box.ParseFile(ms.ToArray());
        Assert.Equal("M4A ", Encoding.ASCII.GetString(root.Children[0].Data[..4]));
        var traks = root.Path("moov").FindAll("trak").ToList();
        Assert.Single(traks);
        Assert.Equal(100u, traks[0].Path("mdia/minf/stbl/stsz").U32(8));
        Assert.Null(traks[0].Path("mdia/minf/stbl").Find("stss"));
        // duration = 100 * 1024 samples
        Assert.Equal(102_400u, traks[0].Path("mdia/mdhd").U32(16));
    }
}

public class Mp3Tests
{
    [Fact]
    public void Lame_EncodesCbrStreamWithInfoTag()
    {
        Assert.True(LameMp3Encoder.IsAvailable, "mp3lame native library missing from runtimes/<rid>/native");
        var pcm = new short[44100 * 2];
        for (var i = 0; i < pcm.Length; i++) { pcm[i] = (short)(10000 * Math.Sin(i * 0.06)); }
        var ms = new MemoryStream();
        using (var enc = new LameMp3Encoder(44100, 128_000))
        {
            for (var i = 0; i < pcm.Length; i += 1000) { enc.Encode(pcm.AsSpan(i, Math.Min(1000, pcm.Length - i)), ms); }
            enc.Finish(ms);
        }
        var b = ms.ToArray();
        Assert.Equal(0xFF, b[0]);
        Assert.Equal(0xE0, b[1] & 0xE0);
        Assert.InRange((double)b.Length, 2 * 128_000 / 8 * 0.9, 2 * 128_000 / 8 * 1.2);
        Assert.True(b.AsSpan(0, 120).IndexOf("Info"u8) > 0 || b.AsSpan(0, 120).IndexOf("Xing"u8) > 0, "LAME tag frame at start");
    }
}

public class ClockLockedTrackTests
{
    private const long T0 = 5_000_000;

    private sealed class Sim
    {
        public readonly List<short> Out = [];
        public readonly ClockLockedTrack Track;
        public long Now = T0;

        public Sim() => Track = new ClockLockedTrack(T0, () => Now, c => Out.AddRange(c));

        /// <summary>Ramp source (value = index / 80) at <paramref name="srcRate"/>, delivered in 512-sample packets after <paramref name="latency"/> s.
        /// Packets whose capture time lies in a dropout window [a,b) are lost.</summary>
        public void Run(double srcRate, double seconds, double latency, (double A, double B)? drop = null, Func<int, double>? jitter = null)
        {
            var packet = 512;
            Track.SetEnd(T0 + (long)(seconds * 1e7));
            for (var k = 0; ; k++)
            {
                var capEnd = (k + 1) * packet / srcRate;
                var arrival = capEnd + latency + (jitter?.Invoke(k) ?? 0);
                if (arrival >= seconds + 0.4) { break; }
                if (drop is { } d && capEnd > d.A && capEnd <= d.B) { continue; }
                var data = new short[packet];
                for (var i = 0; i < packet; i++) { data[i] = (short)Math.Round((k * packet + i) / 80.0); }
                Now = T0 + (long)(arrival * 1e7);
                Track.Write(data, 44100, Now);
            }
            Now = T0 + (long)((seconds + 0.4) * 1e7);
            Track.Finish(Now);
        }
    }

    /// <summary>Time offset (s) between output position and the source time of the sample value found there.</summary>
    private static double Lag(List<short> o, int n, double srcRate)
    {
        var avg = 0.0;
        for (var i = -200; i < 200; i++) { avg += o[n + i]; }
        avg /= 400;
        return n / 44100.0 - avg * 80 / srcRate;
    }

    [Theory]
    [InlineData(1.002)]
    [InlineData(0.998)]
    [InlineData(1.0)]
    public void Drift_IsAbsorbed_ExactLengthAndStableAlignment(double factor)
    {
        var sim = new Sim();
        sim.Run(44100 * factor, 50.0, latency: 0.12);
        Assert.Equal((long)(50.0 * 44100), sim.Out.Count);
        Assert.Equal(sim.Out.Count, sim.Track.OutputSamples);
        Assert.Equal(0, sim.Track.Gaps);
        Assert.InRange(sim.Track.Ratio, 0.995, 1.005);
        var early = Lag(sim.Out, 10 * 44100, 44100 * factor);
        var late = Lag(sim.Out, 45 * 44100, 44100 * factor);
        Assert.True(Math.Abs(late - early) < 0.004, $"alignment moved {1000 * (late - early):F2} ms");
        Assert.True(Math.Abs(late) < 0.05, $"lag {late * 1000:F1} ms");
    }

    [Fact]
    public void FirstOutputSample_IsFirstReceivedSample()
    {
        var sim = new Sim();
        sim.Run(44100, 3.0, latency: 0.05);
        Assert.Equal(0, sim.Out[0]);
        Assert.True(sim.Out[200] is >= 1 and <= 4);
    }

    [Fact]
    public void Jitter_DoesNotCreateGapsOrLoseSamples()
    {
        var rnd = new Random(3);
        var sim = new Sim();
        sim.Run(44100 * 1.001, 30.0, latency: 0.15, jitter: _ => rnd.NextDouble() * 0.04);
        Assert.Equal((long)(30.0 * 44100), sim.Out.Count);
        Assert.Equal(0, sim.Track.Gaps);
        Assert.Equal(0, sim.Track.SilenceSamples);
    }

    [Fact]
    public void Dropout_IsFilledWithSilence_AndLaterAudioStaysInPlace()
    {
        var sim = new Sim();
        sim.Run(44100, 20.0, latency: 0.1, drop: (8.0, 10.0));
        Assert.Equal((long)(20.0 * 44100), sim.Out.Count);
        Assert.Equal(1, sim.Track.Gaps);
        // silence inside the gap (ramp values are non-zero there otherwise)
        var inside = sim.Out.Skip((int)(8.5 * 44100)).Take((int)(1.0 * 44100));
        Assert.All(inside, v => Assert.Equal(0, v));
        // after the gap the audio is back at the right time: lag equals the pre-gap lag within 100 ms
        var before = Lag(sim.Out, 6 * 44100, 44100);
        var after = Lag(sim.Out, 15 * 44100, 44100);
        Assert.True(Math.Abs(after - before) < 0.08, $"lag before {before}, after {after}");
    }

    [Fact]
    public void LateStart_IsPrecededBySilence()
    {
        var sim = new Sim();
        // first packet arrives 2 s after t0
        sim.Run(44100, 6.0, latency: 2.0);
        Assert.Equal((long)(6.0 * 44100), sim.Out.Count);
        Assert.Equal(0, sim.Out[44100]);
        Assert.Equal(0, sim.Track.Gaps);
    }

    [Fact]
    public void NoInput_FinishPadsExactSilence()
    {
        long now = T0;
        var n = 0;
        var t = new ClockLockedTrack(T0, () => now, c => n += c.Length);
        now = T0 + 25_000_000;
        t.Finish();
        Assert.Equal(110250, n);
    }
}

public class TakeExportTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "smartmike_take_" + Guid.NewGuid().ToString("N"), "20260101_120000");
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void SingleFile_IsReturnedDirectly_MultipleBecomeZip()
    {
        var folder = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(folder, "a.wav"), "A");
            Assert.Equal(Path.Combine(folder, "a.wav"), TakeExport.GetShareFile(folder));

            File.WriteAllText(Path.Combine(folder, "b.wav"), "B");
            var zip = TakeExport.GetShareFile(folder + Path.DirectorySeparatorChar);
            Assert.Equal(folder + ".zip", zip);
            using (var za = System.IO.Compression.ZipFile.OpenRead(zip))
            {
                Assert.Equal(["a.wav", "b.wav"], za.Entries.Select(e => e.FullName).OrderBy(x => x));
            }
            var stamp = File.GetLastWriteTimeUtc(zip);
            Assert.Equal(zip, TakeExport.GetShareFile(folder)); // reused
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(zip));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, true);
        }
    }
}

public class HubRecordingTests
{
    private sealed class FakeBt : IBluetoothPlatform
    {
        public IReadOnlyList<PairedDevice> GetPairedDevices() => [];
        public Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeAudio : IAudioPlatform
    {
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public IAudioSink Open(AudioOutputDevice device, JitterBuffer source, int sampleRate) => throw new NotSupportedException();
    }

    [Fact]
    public void AvailableFormats_DependOnPlatformServices()
    {
        var hub = new MikeHub(new FakeBt(), new FakeAudio());
        var f = hub.AvailableFormats;
        Assert.Contains(RecordingFormat.Wav, f);
        Assert.Contains(RecordingFormat.Mka, f);
        Assert.Contains(RecordingFormat.Mp3, f);
        Assert.DoesNotContain(RecordingFormat.M4a, f);
        Assert.DoesNotContain(RecordingFormat.Mkv, f);
        Assert.DoesNotContain(RecordingFormat.Mp4, f);
    }

    [Fact]
    public void Start_WithoutCameraOrMics_ThrowsReadableErrors()
    {
        var hub = new MikeHub(new FakeBt(), new FakeAudio());
        var root = Path.Combine(Path.GetTempPath(), "smartmike_hub_" + Guid.NewGuid().ToString("N"));
        hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Mkv };
        var ex = Assert.Throws<InvalidOperationException>(() => hub.StartRecordingAll(root));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Wav };
        ex = Assert.Throws<InvalidOperationException>(() => hub.StartRecordingAll(root));
        Assert.Contains("microphone", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(hub.IsRecording);
        Assert.False(Directory.Exists(root));
    }
}

/// <summary>Real capture -> clock-locked track -> WAV / FLAC-in-Matroska take outputs.</summary>
[Collection("celt")]
public class EndToEndTests
{
    private static short[] CaptureLeft()
    {
        var r = new FrameReader();
        var list = new List<ReceivedFrame>();
        r.Feed(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "record_6s_rx.bin")), list);
        using var dec = new QCeltStreamDecoder();
        var left = new List<short>();
        foreach (var f in list.Where(f => f.IsCommand && f.Command == Command.AudioStreamVer))
        {
            dec.Decode(f.Payload[4..], (h, pcm) =>
            {
                for (var i = 0; i < pcm.Length; i += 2) { left.Add(pcm[i]); }
            });
        }
        return left.ToArray();
    }

    [Theory]
    [InlineData(MatroskaAudioCodec.Flac)]
    [InlineData(MatroskaAudioCodec.Pcm)]
    public void CaptureToWavAndMka(MatroskaAudioCodec codec)
    {
        var input = CaptureLeft();
        Assert.True(input.Length > 200_000);
        var dir = Path.Combine(Path.GetTempPath(), "smartmike_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wavPath = Path.Combine(dir, "mic.wav");
            var mkaPath = Path.Combine(dir, "take.mka");
            var wav = new PerMicOutput([new WavSink(wavPath)], [wavPath]);
            var mka = new MatroskaOutput(mkaPath, "take", ["SmartMike+ 0D5E", "SmartMike+ 9F00"], codec, null, 0);

            const long t0 = 1_000_000;
            long now = t0;
            var track = new ClockLockedTrack(t0, () => now, c =>
            {
                wav.Mics[0].Write(c);
                mka.Mics[0].Write(c);
                mka.Mics[1].Write(c);
            });
            // the real capture arrives ~0.2 % fast relative to the host clock
            const double rate = 44100 * 1.002;
            for (var i = 0; i + 512 <= input.Length; i += 512)
            {
                now = t0 + (long)((i + 512) / rate * 1e7) + 800_000;
                track.Write(input.AsSpan(i, 512), 44100, now);
            }
            var seconds = input.Length / rate;
            var end = t0 + (long)(seconds * 1e7);
            track.SetEnd(end);
            now = end + 3_000_000;
            track.Finish(now);
            wav.Complete();
            mka.Complete();

            var expected = (long)(seconds * 44100);
            Assert.InRange(track.OutputSamples, expected - 1, expected + 1);

            var wb = File.ReadAllBytes(wavPath);
            Assert.Equal(44 + 2 * track.OutputSamples, wb.Length);
            var wavPcm = new short[track.OutputSamples];
            Buffer.BlockCopy(wb, 44, wavPcm, 0, wavPcm.Length * 2);
            // content is the capture, resampled by 0.2 %: energy matches closely
            double Energy(IEnumerable<short> s) => s.Select(v => (double)v * v).Average();
            Assert.InRange(Energy(wavPcm) / Energy(input), 0.95, 1.05);

            var root = EbmlNode.ParseFile(File.ReadAllBytes(mkaPath));
            var seg = root.One(0x18538067)!;
            var tracks = seg.One(0x1654AE6B)!.All(0xAE).ToList();
            Assert.Equal(2, tracks.Count);
            Assert.Equal(codec == MatroskaAudioCodec.Flac ? "A_FLAC" : "A_PCM/INT/LIT", tracks[0].One(0x86)!.Text);
            Assert.Equal("SmartMike+ 9F00", tracks[1].One(0x536E)!.Text);

            // concatenate track 1's blocks and compare with the WAV sample-for-sample
            var payload = new MemoryStream();
            if (codec == MatroskaAudioCodec.Flac) { payload.Write(tracks[0].One(0x63A2)!.Data); }
            foreach (var c in seg.All(0x1F43B675))
            {
                foreach (var b in c.All(0xA3).Where(b => (b.Data[0] & 0x7F) == 1)) { payload.Write(b.Data[4..]); }
            }
            short[] decoded;
            if (codec == MatroskaAudioCodec.Flac)
            {
                decoded = TestFlacDecoder.Decode(payload.ToArray()).Samples;
            }
            else
            {
                var pb = payload.ToArray();
                decoded = new short[pb.Length / 2];
                Buffer.BlockCopy(pb, 0, decoded, 0, pb.Length);
            }
            Assert.Equal(wavPcm, decoded);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>Full hub path with fake sessions (state and PCM injected by reflection), fake camera and fake AAC encoder.</summary>
public class HubTakeTests
{
    private sealed class FakeBt : IBluetoothPlatform
    {
        public IReadOnlyList<PairedDevice> GetPairedDevices() => [];
        public Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeAudio : IAudioPlatform
    {
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public IAudioSink Open(AudioOutputDevice device, JitterBuffer source, int sampleRate) => throw new NotSupportedException();
    }

    private sealed class FakeAac : IAacEncoderFactory
    {
        public IAacEncoder Create(int sampleRate, int channels, int bitRate) => new Enc();

        private sealed class Enc : IAacEncoder
        {
            private int _pending;
            public int SampleRate => 44100;
            public int Channels => 1;
            public ReadOnlyMemory<byte> AudioSpecificConfig => new byte[] { 0x12, 0x08 };
            public void Encode(ReadOnlySpan<short> pcm, Action<EncodedSample> output)
            {
                _pending += pcm.Length;
                while (_pending >= 1024) { _pending -= 1024; output(new EncodedSample(new byte[] { 1, 2, 3 }, 0, true)); }
            }
            public void Flush(Action<EncodedSample> output) { if (_pending > 0) { _pending = 0; output(new EncodedSample(new byte[] { 1, 2, 3 }, 0, true)); } }
            public void Dispose() { }
        }
    }

    private sealed class FakeCamera : IVideoCapture
    {
        public CameraInfo Camera { get; } = new("0", "Fake", false);
        public int Width => 640;
        public int Height => 480;
        public int FrameRate => 30;
        public int RotationDegrees => 90;
        public event Action<PreviewFrame>? PreviewFrameReady { add { } remove { } }
        public event Action<EncodedSample>? SampleEncoded;
        public bool Encoding;
        public void StartEncoding()
        {
            Encoding = true;
            Task.Run(async () =>
            {
                byte[] key = [0, 0, 0, 1, 0x67, 0x42, 0xC0, 0x1F, 0xDA, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80, 0, 0, 1, 0x65, 1, 2, 3];
                byte[] delta = [0, 0, 1, 0x41, 9, 9, 9];
                for (var i = 0; i < 20 && Encoding; i++)
                {
                    SampleEncoded?.Invoke(new EncodedSample(i == 0 ? key : delta, MediaClock.Now, i == 0));
                    await Task.Delay(33);
                }
            });
        }
        public void StopEncoding() => Encoding = false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVideo : IVideoPlatform
    {
        public IReadOnlyList<CameraInfo> GetCameras() => [];
        public Task<IVideoCapture> OpenAsync(CameraInfo camera, VideoSettings settings, CancellationToken ct = default) => Task.FromResult<IVideoCapture>(new FakeCamera());
    }

    private static (MikeHub Hub, List<MikeSession> Sessions) NewHub(int mics, bool services)
    {
        var hub = new MikeHub(new FakeBt(), new FakeAudio(), services ? new FakeAac() : null, services ? new FakeVideo() : null);
        var sessions = new List<MikeSession>();
        for (var i = 0; i < mics; i++)
        {
            var ch = hub.Add(new PairedDevice(new BluetoothAddress(0x2836380D5E00UL + (ulong)i), "SmartMike+"));
            typeof(MikeSession).GetProperty("State")!.SetValue(ch.Session, MikeState.Streaming);
            sessions.Add(ch.Session);
        }
        return (hub, sessions);
    }

    private static void Raise(MikeSession s, short[] pcm)
    {
        var f = (PcmHandler?)typeof(MikeSession).GetField("PcmReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(s);
        f?.Invoke(s, pcm, 44100);
    }

    private static async Task Feed(List<MikeSession> sessions, double seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sent = 0L;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var due = (long)(sw.Elapsed.TotalSeconds * 44100);
            while (sent + 512 <= due)
            {
                var pcm = new short[512];
                for (var i = 0; i < 512; i++) { pcm[i] = (short)(9000 * Math.Sin((sent + i) * 0.05)); }
                foreach (var s in sessions) { Raise(s, pcm); }
                sent += 512;
            }
            await Task.Delay(5);
        }
    }

    [Theory]
    [InlineData(RecordingFormat.Wav, 2, 2)]
    [InlineData(RecordingFormat.Mp3, 2, 2)]
    [InlineData(RecordingFormat.M4a, 2, 2)]
    [InlineData(RecordingFormat.Mka, 2, 1)]
    [InlineData(RecordingFormat.Mkv, 2, 1)]
    [InlineData(RecordingFormat.Mp4, 2, 1)]
    public async Task RecordsTake(RecordingFormat format, int mics, int expectedFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), "smartmike_hubtake_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(mics, true);
            var camera = new FakeCamera();
            hub.Camera = camera;
            hub.RecordingSettings = new RecordingSettings { Format = format };
            Assert.Contains(format, hub.AvailableFormats);
            var folder = hub.StartRecordingAll(root);
            Assert.True(hub.IsRecording);
            Assert.Equal(folder, hub.RecordingFolder);
            Assert.All(hub.Channels, c => Assert.NotNull(c.RecordingPath));
            await Feed(sessions, 1.5);
            Assert.InRange(hub.Channels[0].RecordedDuration.TotalSeconds, 1.0, 1.6);
            var take = await hub.StopRecordingAllAsync();
            Assert.False(hub.IsRecording);
            Assert.Null(hub.Channels[0].RecordingPath);
            Assert.Equal(format, take.Format);
            Assert.Equal(folder, take.Folder);
            Assert.Equal(expectedFiles, take.Files.Count);
            Assert.Empty(take.Errors);
            Assert.InRange(take.Duration.TotalSeconds, 1.4, 2.5);
            Assert.All(take.Files, f => Assert.True(new FileInfo(f).Length > 1000, f));
            Assert.False(camera.Encoding);
            var share = TakeExport.GetShareFile(folder);
            Assert.Equal(expectedFiles == 1, share.EndsWith(format.Extension()));
            if (format == RecordingFormat.Mp4 || format == RecordingFormat.Mkv)
            {
                Assert.True(File.ReadAllBytes(take.Files[0]).AsSpan().IndexOf(Encoding.UTF8.GetBytes(hub.Channels[0].Session.DisplayName)) > 0);
            }
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }
}
