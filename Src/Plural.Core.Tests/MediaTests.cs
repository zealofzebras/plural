using System.Buffers.Binary;
using System.Text;
using Plural.Core.Audio;
using Plural.Core.Media;
using Plural.Core.Platform;
using Plural.Core.Recording;

namespace Plural.Core.Tests;

/// <summary>Writers for each format we produce, used to feed the readers.</summary>
internal static class MediaFixtures
{
    public static short[] Ramp(int n) => Enumerable.Range(0, n).Select(i => (short)(i % 30000)).ToArray();

    public static short[] Tone(int n, double freq, double rate = 44100, double amp = 10000) =>
        Enumerable.Range(0, n).Select(i => (short)Math.Round(amp * Math.Sin(2 * Math.PI * freq * i / rate))).ToArray();

    public static byte[] AnnexBKeyFrame()
    {
        byte[] sps = [0x67, 0x42, 0xC0, 0x1F, 0xDA, 0x01, 0x40, 0x16];
        byte[] pps = [0x68, 0xCE, 0x3C, 0x80];
        byte[] idr = [0x65, 0x88, 0x84, 0x00, 0x10, 0xFF];
        return [0, 0, 0, 1, .. sps, 0, 0, 0, 1, .. pps, 0, 0, 1, .. idr];
    }

    public static byte[] Wav(short[] pcm, int rate, int channels)
    {
        var ms = new MemoryStream();
        var h = new byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(36 + pcm.Length * 2));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)(rate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)(pcm.Length * 2));
        ms.Write(h);
        foreach (var s in pcm) { var b = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, s); ms.Write(b); }
        return ms.ToArray();
    }

    /// <summary>MKA with one FLAC track whose first block is at <paramref name="startTicks"/>.</summary>
    public static byte[] MkaFlac(short[] pcm, string name = "Mic", long startTicks = 0, int skipBlock = -1, bool crash = false)
    {
        var ms = new MemoryStream();
        using (var w = new MatroskaWriter(crash ? new NonSeekableStream(ms) : ms, "t", leaveOpen: true))
        {
            var flac = new FlacEncoder();
            var t = w.AddAudio(name, "A_FLAC", 44100, 1, 16, flac.BuildHeader());
            long samples = 0;
            var block = 0;
            FlacFrameHandler h = (f, n) =>
            {
                if (block++ != skipBlock) { w.WriteBlock(t, startTicks + samples * 10_000_000 / 44100, f); }
                samples += n;
            };
            flac.Encode(pcm, h);
            flac.Flush(h);
        }
        return ms.ToArray();
    }

    public static byte[] MkaPcm(short[] pcm, int blockSamples = 4096)
    {
        var ms = new MemoryStream();
        using (var w = new MatroskaWriter(ms, "t", leaveOpen: true))
        {
            var t = w.AddAudio("Pcm", "A_PCM/INT/LIT", 44100, 1, 16);
            for (var i = 0; i < pcm.Length; i += blockSamples)
            {
                var n = Math.Min(blockSamples, pcm.Length - i);
                var b = new byte[n * 2];
                for (var k = 0; k < n; k++) { BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2 * k), pcm[i + k]); }
                w.WriteBlock(t, (long)i * 10_000_000 / 44100, b);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Video (fake H.264, key frame every 30) + two FLAC tracks, <paramref name="seconds"/> long. Returns the file and the video block payloads.</summary>
    public static (byte[] File, List<byte[]> Video) Mkv(int seconds, int rotation = 90, bool crash = false)
    {
        var ms = new MemoryStream();
        var key = AnnexBKeyFrame();
        var video = new List<byte[]>();
        using (var w = new MatroskaWriter(crash ? new NonSeekableStream(ms) : ms, "Take", leaveOpen: true))
        {
            var v = w.AddVideo("Video", 1280, 720, 30, rotation);
            var flacs = new[] { new FlacEncoder(), new FlacEncoder() };
            var a = new[] { w.AddAudio("Mic A", "A_FLAC", 44100, 1, 16, flacs[0].BuildHeader()), w.AddAudio("Mic B", "A_FLAC", 44100, 1, 16, flacs[1].BuildHeader()) };
            w.SetCodecPrivate(v, H264.BuildAvcC(key)!);
            var items = new List<(long Ts, int Order, Action Write)>();
            var pcm = Tone(44100 * seconds, 440);
            for (var m = 0; m < 2; m++)
            {
                var mm = m;
                long samples = 0;
                FlacFrameHandler h = (f, n) =>
                {
                    var data = f.ToArray();
                    var ts = samples * 10_000_000 / 44100;
                    items.Add((ts, 1, () => w.WriteBlock(a[mm], ts, data)));
                    samples += n;
                };
                flacs[m].Encode(pcm, h);
                flacs[m].Flush(h);
            }
            for (var i = 0; i < 30 * seconds; i++)
            {
                var ts = i * 10_000_000L / 30;
                var k = i;
                var payload = H264.ToLengthPrefixed(key).Concat(new byte[] { (byte)i }).ToArray();
                video.Add(payload);
                items.Add((ts, 0, () => w.WriteBlock(v, ts, payload, keyFrame: k % 30 == 0)));
            }
            foreach (var it in items.OrderBy(x => x.Ts).ThenBy(x => x.Order)) { it.Write(); }
        }
        return (ms.ToArray(), video);
    }

    public static readonly byte[] Avcc = [1, 0x42, 0xC0, 0x1F, 0xFF, 0xE1, 0, 2, 0x67, 0x42, 1, 0, 1, 0x68];

    /// <summary>Fragmented MP4: video (30 fps, key every 30, starting at <paramref name="videoStart"/>) + <paramref name="audioTracks"/> fake AAC tracks.</summary>
    public static (byte[] File, List<byte[]> Video) Mp4(int seconds, int rotation = 90, long videoStart = 0, int audioTracks = 1, int priming = 0, bool crash = false)
    {
        var ms = new MemoryStream();
        var video = new List<byte[]>();
        using (var w = new Mp4Writer(crash ? new NonSeekableStream(ms) : ms, audioOnly: false, leaveOpen: true))
        {
            var v = w.AddVideo("Video", 1920, 1080, rotation);
            v.AvcC = Avcc;
            var a = Enumerable.Range(0, audioTracks).Select(i => w.AddAudio("Mic " + i, 44100, 1, [0x12, 0x08])).ToList();
            foreach (var t in a) { t.PrimingSamples = priming; }
            var audioFrames = 0;
            for (var i = 0; i < 30 * seconds; i++)
            {
                var payload = Enumerable.Repeat((byte)i, 40 + i).ToArray();
                video.Add(payload);
                w.WriteSample(v, videoStart + i * 10_000_000L / 30, payload, i % 30 == 0);
                while (audioFrames * 1024.0 / 44100 <= (i + 1) / 30.0)
                {
                    foreach (var t in a) { w.WriteSample(t, 0, BitConverter.GetBytes(audioFrames)); }
                    audioFrames++;
                }
            }
        }
        return (ms.ToArray(), video);
    }

    public static byte[] M4a(int packets, int priming = 0, int audioStart = 0, bool crash = false)
    {
        var ms = new MemoryStream();
        using (var w = new Mp4Writer(crash ? new NonSeekableStream(ms) : ms, audioOnly: true, leaveOpen: true))
        {
            var t = w.AddAudio("Mic", 44100, 1, [0x12, 0x08]);
            t.PrimingSamples = priming;
            for (var k = 0; k < packets; k++) { w.WriteSample(t, 0, BitConverter.GetBytes(k + audioStart)); }
        }
        return ms.ToArray();
    }

    public static void Near(long expected, long actual, long tolerance) => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    public static List<MediaPacket> ReadAll(IMediaReader r)
    {
        var l = new List<MediaPacket>();
        while (r.TryRead(out var p)) { l.Add(p with { Data = p.Data.ToArray() }); }
        return l;
    }

    public static List<MediaPacket> ReadAll(byte[] file)
    {
        using var r = MediaReader.Open(new MemoryStream(file));
        return ReadAll(r);
    }

    public static short[] DecodeFlac(IEnumerable<MediaPacket> packets, byte[] codecPrivate)
    {
        var dec = new FlacDecoder(codecPrivate);
        var all = new List<short>();
        var buf = new short[dec.MaxBlockSize];
        foreach (var p in packets)
        {
            var n = dec.Decode(p.Data.Span, buf);
            all.AddRange(buf.AsSpan(0, n));
        }
        return all.ToArray();
    }
}

internal sealed class NonSeekableStream(Stream inner) : Stream
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

internal sealed class TempFiles : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plural_media_" + Guid.NewGuid().ToString("N"));
    public TempFiles() => Directory.CreateDirectory(_dir);
    public string Write(string name, byte[] data) { var p = Path.Combine(_dir, name); File.WriteAllBytes(p, data); return p; }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }
}

/// <summary>Decoder factory whose "AAC" packets are an int32 k and decode to 1024 samples of value k (so output position is checkable).</summary>
internal sealed class FakeAudioDecoderFactory : IAudioDecoderFactory
{
    public int Created, Resets;
    public bool Supported = true;
    public bool CanDecode(string codec) => Supported && codec is "aac" or "mp3";

    public IAudioDecoder Create(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate)
    {
        if (!Supported) { throw new NotSupportedException(); }
        Created++;
        return new Dec(this, sampleRate, channels);
    }

    private sealed class Dec(FakeAudioDecoderFactory f, int rate, int channels) : IAudioDecoder
    {
        public int SampleRate => rate;
        public int Channels => channels;
        public void Decode(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<short>> output)
        {
            var k = (short)BinaryPrimitives.ReadInt32LittleEndian(packet);
            var pcm = new short[1024 * channels];
            Array.Fill(pcm, k);
            output(pcm);
        }
        public void Drain(Action<ReadOnlySpan<short>> output) { }
        public void Reset() => f.Resets++;
        public void Dispose() { }
    }
}

public class WavReaderTests
{
    [Fact]
    public void RoundTrip_OwnWavWriter()
    {
        using var tmp = new TempFiles();
        var path = Path.Combine(Path.GetTempPath(), "plural_wav_" + Guid.NewGuid().ToString("N") + ".wav");
        var pcm = MediaFixtures.Ramp(10_000);
        try
        {
            using (var w = new WavWriter(path, 44100, 1)) { w.Write(pcm); }
            using var r = MediaReader.Open(path);
            var t = Assert.Single(r.Tracks);
            Assert.Equal(("pcm_s16le", 44100, 1, 16), (t.Codec, t.SampleRate, t.Channels, t.BitsPerSample));
            Assert.Equal(10_000L * 10_000_000 / 44100, r.Duration.Ticks);
            var got = MediaFixtures.ReadAll(r);
            Assert.Equal(3, got.Count); // 4096-frame packets
            Assert.Equal(pcm, got.SelectMany(p => Enumerable.Range(0, p.Data.Length / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(p.Data.Span[(2 * i)..]))).ToArray());
            Assert.Equal(4096L * 10_000_000 / 44100, got[1].Timestamp);
            r.Seek(TimeSpan.FromSeconds(0.1));
            Assert.True(r.TryRead(out var p0));
            Assert.Equal(4410L * 10_000_000 / 44100, p0.Timestamp);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Stereo48k_24Bit_And_Float_AreConvertedToS16()
    {
        // 24-bit stereo, 48 kHz
        var h = new byte[44];
        "RIFF"u8.CopyTo(h); "WAVEfmt "u8.CopyTo(h.AsSpan(8)); "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), 48000 * 6);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 24);
        var data = new List<byte>();
        foreach (var v in new[] { 0x123456, -0x123456, 0x7FFFFF, -0x800000 })
        {
            data.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16)]);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)data.Count);
        var file = h.Concat(data).ToArray();
        using var r = MediaReader.Open(new MemoryStream(file));
        var t = Assert.Single(r.Tracks);
        Assert.Equal(("pcm_s16le", 48000, 2), (t.Codec, t.SampleRate, t.Channels));
        var p = MediaFixtures.ReadAll(r).Single();
        var s = Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt16LittleEndian(p.Data.Span[(2 * i)..])).ToArray();
        Assert.Equal([0x1234, -0x1235, 0x7FFF, -0x8000], s); // top 16 bits (floor)

        // 32-bit float mono
        var f = new byte[44];
        "RIFF"u8.CopyTo(f); "WAVEfmt "u8.CopyTo(f.AsSpan(8)); "data"u8.CopyTo(f.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(20), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(34), 32);
        var fd = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(fd, 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(fd.AsSpan(4), -2f);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(40), 0); // unpatched header (crash): "to the end of the file"
        using var r2 = MediaReader.Open(new MemoryStream(f.Concat(fd).ToArray()));
        var p2 = MediaFixtures.ReadAll(r2).Single();
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(p2.Data.Span));
        Assert.Equal(-32768, BinaryPrimitives.ReadInt16LittleEndian(p2.Data.Span[2..]));
    }

    [Fact]
    public void Open_SniffsContent_NotExtension_AndRejectsGarbage()
    {
        using var tmp = new TempFiles();
        var wav = tmp.Write("looks_like.mp3", MediaFixtures.Wav(MediaFixtures.Ramp(100), 44100, 1));
        using (var r = MediaReader.Open(wav)) { Assert.Equal("pcm_s16le", r.Tracks[0].Codec); }
        var mka = tmp.Write("looks_like.wav", MediaFixtures.MkaPcm(MediaFixtures.Ramp(100)));
        using (var r = MediaReader.Open(mka)) { Assert.Equal("pcm_s16le", r.Tracks[0].Codec); }
        var junk = tmp.Write("junk.mp4", Encoding.ASCII.GetBytes("this is not a media file at all"));
        Assert.Throws<InvalidDataException>(() => MediaReader.Open(junk));
    }
}

public class MatroskaReaderTests
{
    [Fact]
    public void FlacMka_RoundTrip_TracksPacketsAndDecoding()
    {
        var pcm = MediaFixtures.Tone(44100 * 3 + 123, 440);
        var file = MediaFixtures.MkaFlac(pcm, "SmartMike+ 0D5E");
        using var r = MediaReader.Open(new MemoryStream(file));
        var t = Assert.Single(r.Tracks);
        Assert.Equal((0, MediaKind.Audio, "flac", "SmartMike+ 0D5E", 44100, 1, 16), (t.Index, t.Kind, t.Codec, t.Name, t.SampleRate, t.Channels, t.BitsPerSample));
        Assert.Equal("fLaC", Encoding.ASCII.GetString(t.CodecPrivate!, 0, 4));
        Assert.InRange(r.Duration.TotalSeconds, 2.8, 3.2);
        var packets = MediaFixtures.ReadAll(r);
        Assert.All(packets, p => Assert.True(p.KeyFrame));
        Assert.Equal(pcm, MediaFixtures.DecodeFlac(packets, t.CodecPrivate!));
        Assert.Equal(0, packets[0].Timestamp);
        Assert.InRange(packets[1].Timestamp, 929_000, 931_000); // 4096 samples = 92.9 ms
    }

    [Fact]
    public void PcmMka_RoundTrip()
    {
        var pcm = MediaFixtures.Ramp(30_000);
        using var r = MediaReader.Open(new MemoryStream(MediaFixtures.MkaPcm(pcm)));
        Assert.Equal("pcm_s16le", r.Tracks[0].Codec);
        var bytes = MediaFixtures.ReadAll(r).SelectMany(p => p.Data.ToArray()).ToArray();
        Assert.Equal(pcm, Enumerable.Range(0, pcm.Length).Select(i => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2 * i))).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Mkv_VideoTrack_RotationAndAvcC(int rotation)
    {
        var (file, video) = MediaFixtures.Mkv(3, rotation);
        using var r = MediaReader.Open(new MemoryStream(file));
        Assert.Equal(3, r.Tracks.Count);
        var v = r.Tracks[0];
        Assert.Equal((MediaKind.Video, "h264", 1280, 720, rotation, "Video"), (v.Kind, v.Codec, v.Width, v.Height, v.RotationDegrees, v.Name));
        Assert.Equal(1, v.CodecPrivate![0]);
        Assert.Equal((MediaKind.Audio, "Mic A"), (r.Tracks[1].Kind, r.Tracks[1].Name));
        Assert.Equal(("Mic B", 2), (r.Tracks[2].Name, r.Tracks[2].Index));
        var all = MediaFixtures.ReadAll(r);
        var vp = all.Where(p => p.Track == 0).ToList();
        Assert.Equal(90, vp.Count);
        Assert.Equal(video, vp.Select(p => p.Data.ToArray()).ToList());
        Assert.Equal([0, 30, 60], vp.Select((p, i) => (p, i)).Where(x => x.p.KeyFrame).Select(x => x.i));
        Assert.InRange(r.Duration.TotalSeconds, 2.9, 3.1);
    }

    [Fact]
    public void Seek_LandsOnLastKeyFrameAtOrBefore_AndAudioBlock()
    {
        var (file, _) = MediaFixtures.Mkv(4);
        using var r = MediaReader.Open(new MemoryStream(file));
        r.Seek(TimeSpan.FromSeconds(2.5));
        var first = new Dictionary<int, MediaPacket>();
        for (var i = 0; i < 400 && r.TryRead(out var p); i++) { first.TryAdd(p.Track, p); }
        // video: key frame at 2.0 s (frame 60), the last key at or before 2.5
        Assert.True(first[0].KeyFrame);
        MediaFixtures.Near(60L * 10_000_000 / 30, first[0].Timestamp, 10_000);
        // audio: the FLAC block containing 2.5 s starts at most one block (93 ms) earlier
        foreach (var track in new[] { 1, 2 })
        {
            Assert.InRange(first[track].Timestamp, 25_000_000 - 1_000_000, 25_000_000);
        }
        // selecting only audio skips the video payloads
        r.SelectTracks([1]);
        r.Seek(TimeSpan.FromSeconds(1.0));
        Assert.True(r.TryRead(out var a));
        Assert.Equal(1, a.Track);
        Assert.InRange(a.Timestamp, 9_000_000, 10_000_000);
    }

    [Fact]
    public void Seek_WithoutCues_ScansClusters()
    {
        var (file, _) = MediaFixtures.Mkv(4);
        // cut the Cues element (it is the last top-level element) -> reader must scan
        var cues = file.AsSpan().LastIndexOf(new byte[] { 0x1C, 0x53, 0xBB, 0x6B });
        using var r = MediaReader.Open(new MemoryStream(file[..cues]));
        Assert.InRange(r.Duration.TotalSeconds, 3.5, 4.1);
        r.Seek(TimeSpan.FromSeconds(3.2));
        Assert.True(r.TryRead(out var p));
        Assert.True(p.Timestamp <= 32_000_000);
        var vid = new[] { p }.Concat(Enumerable.Range(0, 200).Select(_ => r.TryRead(out var x) ? x : default)).First(x => x.Track == 0);
        Assert.True(vid.KeyFrame);
        MediaFixtures.Near(90L * 10_000_000 / 30, vid.Timestamp, 10_000);
    }

    [Fact]
    public void TruncatedFile_YieldsPacketsBeforeTheCut_AtAnyCutPoint()
    {
        var (file, _) = MediaFixtures.Mkv(5);
        var full = MediaFixtures.ReadAll(file);
        var cues = file.AsSpan().LastIndexOf(new byte[] { 0x1C, 0x53, 0xBB, 0x6B });
        var headerEnd = file.AsSpan().IndexOf(new byte[] { 0x1F, 0x43, 0xB6, 0x75 }) + 12;
        var last = 0;
        for (var cut = headerEnd + 1; cut < cues; cut += 1237)
        {
            using var r = MediaReader.Open(new MemoryStream(file[..cut]));
            var got = MediaFixtures.ReadAll(r);
            Assert.True(got.Count >= last, $"count must not shrink at cut {cut}");
            last = got.Count;
            for (var i = 0; i < got.Count; i++)
            {
                Assert.Equal(full[i].Track, got[i].Track);
                Assert.Equal(full[i].Timestamp, got[i].Timestamp);
                Assert.True(full[i].Data.Span.SequenceEqual(got[i].Data.Span), $"packet {i} payload at cut {cut}");
            }
        }
        Assert.True(last > full.Count / 2);

        // cut in the middle of a cluster, no Cues: still opens with a duration and seeks
        using var mid = MediaReader.Open(new MemoryStream(file[..(cues / 2)]));
        Assert.True(mid.Duration > TimeSpan.Zero);
        mid.Seek(TimeSpan.FromSeconds(1));
        Assert.True(mid.TryRead(out _));
    }

    [Fact]
    public void NonSeekableWriter_UnknownSizeSegmentAndClusters_AreRead()
    {
        var inner = new MemoryStream();
        var pcm = MediaFixtures.Ramp(44100 * 3);
        using (var w = new MatroskaWriter(new NonSeekableStream(inner), "t", leaveOpen: true))
        {
            var t = w.AddAudio("a", "A_PCM/INT/LIT", 44100, 1, 16);
            for (var i = 0; i < pcm.Length; i += 4410)
            {
                var b = new byte[8820];
                for (var k = 0; k < 4410; k++) { BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2 * k), pcm[i + k]); }
                w.WriteBlock(t, (long)i * 10_000_000 / 44100, b);
            }
        }
        using var r = MediaReader.Open(new MemoryStream(inner.ToArray()));
        Assert.InRange(r.Duration.TotalSeconds, 2.8, 3.1);
        Assert.Equal(30, MediaFixtures.ReadAll(r).Count);
        r.Seek(TimeSpan.FromSeconds(1.55));
        Assert.True(r.TryRead(out var p));
        Assert.Equal(15_000_000L, p.Timestamp);
    }

    [Fact]
    public void BlockGroupAndLacing_AreParsed()
    {
        // hand-built: one cluster with a BlockGroup (key = no ReferenceBlock) and a Xiph-laced SimpleBlock of three frames
        byte[] Elem(uint id, byte[] payload) => Ebml.Elem(id, payload);
        var track = Ebml.Master(0xAE, Ebml.UInt(0xD7, 1), Ebml.UInt(0x83, 2), Ebml.Str(0x86, "A_PCM/INT/LIT"), Ebml.UInt(0x23E383, 10_000_000),
            Ebml.Master(0xE1, Ebml.Float64(0xB5, 8000), Ebml.UInt(0x9F, 1), Ebml.UInt(0x6264, 16)));
        byte[] group = Ebml.Master(0xA0, Elem(0xA1, [0x81, 0x00, 0x05, 0x00, 9, 9]), Ebml.UInt(0x9B, 1));
        byte[] laced = Elem(0xA3, [0x81, 0x00, 0x0A, 0x02, 2 /* 3 frames */, 1, 2, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]);
        var cluster = Ebml.Master(0x1F43B675, Ebml.UInt(0xE7, 100), group, laced);
        var seg = Ebml.Master(0x18538067, Ebml.Master(0x1549A966, Ebml.UInt(0x2AD7B1, 1_000_000)), Ebml.Master(0x1654AE6B, track), cluster);
        var file = Ebml.Cat(Ebml.Master(0x1A45DFA3, Ebml.Str(0x4282, "matroska")), seg);
        var p = MediaFixtures.ReadAll(file);
        Assert.Equal(4, p.Count);
        Assert.Equal((105L * 10_000, true), (p[0].Timestamp, p[0].KeyFrame));
        Assert.Equal([9, 9], p[0].Data.ToArray());
        Assert.Equal([[0xAA], [0xBB, 0xCC], [0xDD, 0xEE, 0xFF]], p.Skip(1).Select(x => x.Data.ToArray()).ToArray());
        Assert.Equal([110L * 10_000, 110L * 10_000 + 100_000, 110L * 10_000 + 200_000], p.Skip(1).Select(x => x.Timestamp));
    }
}

public class Mp4ReaderTests
{
    [Fact]
    public void M4a_RoundTrip()
    {
        var file = MediaFixtures.M4a(100);
        using var r = MediaReader.Open(new MemoryStream(file));
        var t = Assert.Single(r.Tracks);
        Assert.Equal(("aac", "Mic", 44100, 1), (t.Codec, t.Name, t.SampleRate, t.Channels));
        Assert.Equal([0x12, 0x08], t.CodecPrivate);
        MediaFixtures.Near(100L * 1024 * 10_000_000 / 44100, r.Duration.Ticks, 10_000); // movie timescale is 1 ms
        var p = MediaFixtures.ReadAll(r);
        Assert.Equal(100, p.Count);
        Assert.All(p, x => Assert.True(x.KeyFrame));
        for (var k = 0; k < 100; k++)
        {
            Assert.Equal(k, BitConverter.ToInt32(p[k].Data.Span));
            Assert.Equal(k * 1024L * 10_000_000 / 44100, p[k].Timestamp);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Mp4_VideoAndAudio_RotationOffsetAndKeyFrames(int rotation)
    {
        var (file, video) = MediaFixtures.Mp4(3, rotation, videoStart: 15_000_000, audioTracks: 2);
        using var r = MediaReader.Open(new MemoryStream(file));
        Assert.Equal(["Video", "Mic 0", "Mic 1"], r.Tracks.Select(t => t.Name));
        var v = r.Tracks[0];
        Assert.Equal((MediaKind.Video, "h264", 1920, 1080, rotation), (v.Kind, v.Codec, v.Width, v.Height, v.RotationDegrees));
        Assert.Equal(MediaFixtures.Avcc, v.CodecPrivate);
        Assert.Equal(("aac", 1), (r.Tracks[1].Codec, r.Tracks[1].Channels));
        var all = MediaFixtures.ReadAll(r);
        var vp = all.Where(p => p.Track == 0).ToList();
        Assert.Equal(video, vp.Select(p => p.Data.ToArray()).ToList());
        // the writer's edit list (empty edit of 1.5 s) comes back as the first video timestamp
        MediaFixtures.Near(15_000_000L, vp[0].Timestamp, 20_000);
        MediaFixtures.Near(15_000_000L + 10_000_000, vp[30].Timestamp, 20_000);
        Assert.Equal([0, 30, 60], vp.Select((p, i) => (p, i)).Where(x => x.p.KeyFrame).Select(x => x.i));
        Assert.Equal(0L, all.First(p => p.Track == 1).Timestamp);
        Assert.InRange(r.Duration.TotalSeconds, 4.4, 4.6); // 1.5 s offset + 3 s video
        // file order: tracks interleave inside a fragment
        Assert.Contains(all.Take(120), p => p.Track == 0);
        Assert.Contains(all.Take(120), p => p.Track == 2);
    }

    [Fact]
    public void Seek_PicksLastKeyFramePerTrack()
    {
        var (file, _) = MediaFixtures.Mp4(4, audioTracks: 1);
        using var r = MediaReader.Open(new MemoryStream(file));
        r.Seek(TimeSpan.FromSeconds(2.5));
        var first = new Dictionary<int, MediaPacket>();
        while (r.TryRead(out var p)) { first.TryAdd(p.Track, p); }
        Assert.True(first[0].KeyFrame);
        MediaFixtures.Near(60L * 10_000_000 / 30, first[0].Timestamp, 10_000);
        Assert.Equal(107, BitConverter.ToInt32(first[1].Data.Span)); // 2.5 s * 44100 / 1024 = 107.7 -> frame 107
        r.SelectTracks([1]);
        r.Seek(TimeSpan.FromSeconds(1));
        Assert.True(r.TryRead(out var a));
        Assert.Equal((1, 43), (a.Track, BitConverter.ToInt32(a.Data.Span)));
    }

    [Fact]
    public void Priming_ComesBackAsNegativeTimestamp_ViaEditList()
    {
        using var r = MediaReader.Open(new MemoryStream(MediaFixtures.M4a(10, priming: 2112)));
        var p = MediaFixtures.ReadAll(r);
        Assert.Equal(-2112L * 10_000_000 / 44100, p[0].Timestamp);
        Assert.Equal((1024 - 2112L) * 10_000_000 / 44100, p[1].Timestamp);
        MediaFixtures.Near((10L * 1024 - 2112) * 10_000_000 / 44100, r.Duration.Ticks, 10_000);
    }

    [Fact]
    public void Fragmented_TruncatedAtAnyPoint_KeepsEverythingBeforeTheCut()
    {
        var (file, _) = MediaFixtures.Mp4(4, audioTracks: 1);
        var full = MediaFixtures.ReadAll(file);
        var root = Mp4Box.ParseFile(file);
        var moovEnd = (int)root.Path("moov").End;
        var mfra = (int)root.Path("mfra").Start;
        var last = 0;
        for (var cut = moovEnd; cut <= mfra; cut += 977)
        {
            var got = MediaFixtures.ReadAll(file[..cut]);
            Assert.True(got.Count >= last, $"count must not shrink at cut {cut}");
            last = got.Count;
            for (var i = 0; i < got.Count; i++)
            {
                Assert.Equal(full[i].Track, got[i].Track);
                Assert.True(full[i].Data.Span.SequenceEqual(got[i].Data.Span));
            }
        }
        Assert.Equal(full.Count, MediaFixtures.ReadAll(file[..mfra]).Count);
        // cut in the middle of the third mdat: the first two fragments plus the samples that are complete
        var mdats = root.FindAll("mdat").ToList();
        var partial = MediaFixtures.ReadAll(file[..(int)((mdats[2].Start + mdats[2].End) / 2)]);
        var twoFragments = MediaFixtures.ReadAll(file[..(int)mdats[1].End]);
        Assert.InRange(partial.Count, twoFragments.Count + 1, full.Count - 1);
        // a cut moof header is simply the end
        var moof3 = root.FindAll("moof").ToList()[2];
        Assert.Equal(twoFragments.Count, MediaFixtures.ReadAll(file[..(int)(moof3.Start + 20)]).Count);
        // and the recovered file still has a duration and seeks
        using var r = MediaReader.Open(new MemoryStream(file[..(int)mdats[1].End]));
        Assert.InRange(r.Duration.TotalSeconds, 3.9, 4.1); // a copy of a finished file keeps the duration patched into its header
        r.Seek(TimeSpan.FromSeconds(1.5));
        Assert.True(r.TryRead(out _));
    }

    [Fact]
    public void CrashWithoutDispose_KeepsCompletedFragments()
    {
        var ms = new MemoryStream();
        var w = new Mp4Writer(ms, audioOnly: false, leaveOpen: true);
        var v = w.AddVideo("Video", 640, 480);
        v.AvcC = MediaFixtures.Avcc;
        var a = w.AddAudio("Mic", 44100, 1, [0x12, 0x08]);
        var audio = 0;
        for (var i = 0; i < 75; i++)
        {
            w.WriteSample(v, i * 10_000_000L / 30, new byte[] { (byte)i, 1, 2 }, i % 30 == 0);
            while (audio * 1024.0 / 44100 <= (i + 1) / 30.0) { w.WriteSample(a, 0, BitConverter.GetBytes(audio++)); }
        }
        var snapshot = ms.ToArray(); // the process dies here: no Dispose, no mfra, no duration patching
        w.Dispose();
        var got = MediaFixtures.ReadAll(snapshot);
        Assert.Equal(60, got.Count(p => p.Track == 0)); // fragments hold frames 0-29 and 30-59; 60-74 were still in memory
        Assert.Equal(2, Mp4Box.ParseFile(snapshot).FindAll("moof").Count());
        Assert.Null(Mp4Box.ParseFile(snapshot).Find("mfra"));
        using var r = MediaReader.Open(new MemoryStream(snapshot));
        Assert.InRange(r.Duration.TotalSeconds, 1.9, 2.1);
    }

    [Fact]
    public void NonSeekableStream_StillWritesAReadableFile()
    {
        var inner = new MemoryStream();
        using (var w = new Mp4Writer(new NonSeekableStream(inner), audioOnly: true, leaveOpen: true))
        {
            var t = w.AddAudio("Mic", 44100, 1, [0x12, 0x08]);
            for (var k = 0; k < 120; k++) { w.WriteSample(t, 0, BitConverter.GetBytes(k)); }
        }
        var file = inner.ToArray();
        Assert.Equal(0u, Mp4Box.ParseFile(file).Path("moov/mvex/mehd").U32(8)); // durations can't be patched without seeking
        using var r = MediaReader.Open(new MemoryStream(file));
        Assert.Equal(120, MediaFixtures.ReadAll(r).Count);
        Assert.Equal(120L * 1024 * 10_000_000 / 44100, r.Duration.Ticks); // no patched header: found from the sample tables, exact
    }

    [Fact]
    public void LateAudioSpecificConfig_MoovWaitsForIt()
    {
        var ms = new MemoryStream();
        using (var w = new Mp4Writer(ms, audioOnly: true, leaveOpen: true))
        {
            var t = w.AddAudio("Mic", 44100, 1, []);
            for (var k = 0; k < 100; k++) { w.WriteSample(t, 0, new byte[] { 1, 2, 3, (byte)k }); }
            Assert.Equal(0, ms.Length); // nothing can be written before the config is known
            t.AudioSpecificConfig = [0x11, 0x90]; // reported by the encoder when it is flushed
        }
        using var r = MediaReader.Open(new MemoryStream(ms.ToArray()));
        Assert.Equal([0x11, 0x90], r.Tracks[0].CodecPrivate);
        Assert.Equal(100, MediaFixtures.ReadAll(r).Count);
        var root = Mp4Box.ParseFile(ms.ToArray());
        Assert.Equal("moov", root.Children[1].Type);
    }

    [Fact]
    public void LateConfig_AfterManyBufferedBytes_UsesSynthesizedAscAndPatchesIt()
    {
        var ms = new MemoryStream();
        using (var w = new Mp4Writer(ms, audioOnly: true, leaveOpen: true))
        {
            var t = w.AddAudio("Mic", 44100, 1, []);
            var big = new byte[40_000];
            for (var k = 0; k < 120; k++) { w.WriteSample(t, 0, big); }
            Assert.True(ms.Length > 0, "buffer limit reached: moov and fragments are on disk");
            t.AudioSpecificConfig = [0x12, 0x10]; // different from the synthesized AAC-LC 44.1 kHz mono config, same length
        }
        using var r = MediaReader.Open(new MemoryStream(ms.ToArray()));
        Assert.Equal([0x12, 0x10], r.Tracks[0].CodecPrivate);
        Assert.Equal(120, MediaFixtures.ReadAll(r).Count);

        var ms2 = new MemoryStream();
        using (var w = new Mp4Writer(ms2, audioOnly: true, leaveOpen: true))
        {
            var t = w.AddAudio("Mic", 44100, 1, []);
            for (var k = 0; k < 120; k++) { w.WriteSample(t, 0, new byte[40_000]); }
        }
        using var r2 = MediaReader.Open(new MemoryStream(ms2.ToArray()));
        Assert.Equal([0x12, 0x08], r2.Tracks[0].CodecPrivate); // synthesized AAC-LC 44.1 kHz mono
    }

    [Fact]
    public void VideoWaitsForAvcCAndFirstSample_ThenFragmentsStartOnKeyFrames()
    {
        var ms = new MemoryStream();
        using (var w = new Mp4Writer(ms, audioOnly: false, leaveOpen: true))
        {
            var v = w.AddVideo("Video", 640, 480);
            var a = w.AddAudio("Mic", 44100, 1, [0x12, 0x08]);
            for (var k = 0; k < 20; k++) { w.WriteSample(a, 0, new byte[] { 1 }); }
            Assert.Equal(0, ms.Length);
            v.AvcC = MediaFixtures.Avcc;
            Assert.Equal(0, ms.Length); // still no video sample
            w.WriteSample(v, 5_000_000, new byte[] { 9, 9 }, true);
            Assert.True(ms.Length > 0);
        }
        var root = Mp4Box.ParseFile(ms.ToArray());
        Assert.Equal("ftyp", root.Children[0].Type);
        Assert.Equal("moov", root.Children[1].Type);
        // the 0.5 s start offset made it into the edit list: empty edit of 500 ms
        var elst = root.Path("moov").FindAll("trak").First().Path("edts/elst");
        Assert.Equal(500u, elst.U32(8));
    }
}

public class Mp3ReaderTests
{
    [Fact]
    public void Lame_RoundTrip_FramesDurationAndSeek()
    {
        Assert.True(LameMp3Encoder.IsAvailable, "mp3lame native library missing from runtimes/<rid>/native");
        var pcm = MediaFixtures.Tone(44100 * 3, 440);
        var ms = new MemoryStream();
        using (var enc = new LameMp3Encoder(44100, 128_000))
        {
            for (var i = 0; i < pcm.Length; i += 1000) { enc.Encode(pcm.AsSpan(i, Math.Min(1000, pcm.Length - i)), ms); }
            enc.Finish(ms);
        }
        var file = ms.ToArray();
        using var r = MediaReader.Open(new MemoryStream(file));
        var t = Assert.Single(r.Tracks);
        Assert.Equal(("mp3", 44100, 1), (t.Codec, t.SampleRate, t.Channels));
        Assert.InRange(r.Duration.TotalSeconds, 2.95, 3.1);
        var packets = MediaFixtures.ReadAll(r);
        Assert.InRange(packets.Count, 3 * 44100 / 1152 - 2, 3 * 44100 / 1152 + 4);
        Assert.All(packets, p => { Assert.Equal(0xFF, p.Data.Span[0]); Assert.Equal(0xE0, p.Data.Span[1] & 0xE0); });
        Assert.Equal(1152L * 10_000_000 / 44100, packets[1].Timestamp);
        Assert.True(packets.Sum(p => p.Data.Length) <= file.Length);
        r.Seek(TimeSpan.FromSeconds(1.0));
        Assert.True(r.TryRead(out var p1));
        Assert.InRange(p1.Timestamp, 10_000_000 - 270_000, 10_000_000);
        // a truncated last frame is dropped, not thrown on
        using var cut = MediaReader.Open(new MemoryStream(file[..(file.Length - 100)]));
        Assert.Equal(packets.Count - 1, MediaFixtures.ReadAll(cut).Count);
    }

    [Fact]
    public void Id3Tag_IsSkipped_AndSyntheticFramesAreSplit()
    {
        // 20 CBR frames, MPEG-1 Layer III 128 kbit/s 44.1 kHz mono: 417/418 bytes
        var frames = new List<byte>();
        frames.AddRange("ID3"u8.ToArray()); frames.AddRange(new byte[] { 3, 0, 0, 0, 0, 0, 20 }); frames.AddRange(new byte[20]);
        var sizes = new List<int>();
        double acc = 0;
        for (var i = 0; i < 20; i++)
        {
            acc += 144.0 * 128000 / 44100;
            var len = (int)acc; acc -= len;
            var pad = len == 418 ? 1 : 0;
            frames.AddRange(new byte[] { 0xFF, 0xFB, (byte)(0x90 | (pad << 1)), 0xC0 });
            frames.AddRange(new byte[len - 4]);
            sizes.Add(len);
        }
        using var r = MediaReader.Open(new MemoryStream(frames.ToArray()));
        var p = MediaFixtures.ReadAll(r);
        Assert.Equal(20, p.Count);
        Assert.Equal(sizes, p.Select(x => x.Data.Length));
        Assert.InRange(r.Duration.TotalSeconds, 0.5, 0.53);
    }
}

public class FlacDecoderTests
{
    private static byte[] Encode(short[] pcm, int chunk = 3000, int sampleRate = 44100)
    {
        var ms = new MemoryStream();
        using (var w = new FlacFileWriter(ms, sampleRate, leaveOpen: true))
        {
            for (var i = 0; i < pcm.Length; i += chunk) { w.Write(pcm.AsSpan(i, Math.Min(chunk, pcm.Length - i))); }
        }
        return ms.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(70_000)]
    public void RoundTrip_FlacEncoder_BitExact(int n)
    {
        var rnd = new Random(n);
        var pcm = MediaFixtures.Tone(n, 300).Select(s => (short)(s + rnd.Next(-200, 200))).ToArray();
        var packets = MediaFixtures.ReadAll(MkaOf(pcm));
        Assert.Equal(pcm, MediaFixtures.DecodeFlac(packets, ReaderCodecPrivate(pcm)));
    }

    private static byte[] MkaOf(short[] pcm) => MediaFixtures.MkaFlac(pcm);
    private static byte[] ReaderCodecPrivate(short[] pcm) => new FlacEncoder().BuildHeader();

    [Fact]
    public void RoundTrip_Noise_Silence_Extremes_AndStreamInfoOfAFlacFile()
    {
        var rnd = new Random(5);
        var noise = Enumerable.Range(0, 20_000).Select(_ => (short)rnd.Next(short.MinValue, short.MaxValue + 1)).ToArray();
        var odd = new short[12_000];
        for (var i = 0; i < 4096; i++) { odd[i] = i % 2 == 0 ? short.MaxValue : short.MinValue; }
        Array.Fill(odd, (short)-5, 8192, odd.Length - 8192);
        foreach (var pcm in new[] { noise, odd })
        {
            var enc = new FlacEncoder();
            var frames = new List<byte[]>();
            FlacFrameHandler h = (f, n) => frames.Add(f.ToArray());
            enc.Encode(pcm, h);
            enc.Flush(h);
            var dec = new FlacDecoder(enc.BuildHeader());
            Assert.Equal((44100, 1, 16, pcm.Length), (dec.SampleRate, dec.Channels, dec.BitsPerSample, (int)dec.TotalSamples));
            var got = new List<short>();
            var buf = new short[dec.MaxBlockSize];
            foreach (var f in frames) { got.AddRange(buf.AsSpan(0, dec.Decode(f, buf))); }
            Assert.Equal(pcm, got);
            Assert.Equal(0, dec.ErrorFrames);
            // the head of a .flac file written by FlacFileWriter parses too
            Assert.Equal(pcm.Length, (int)new FlacDecoder(Encode(pcm).AsSpan(0, 42)).TotalSamples);
        }
    }

    [Fact]
    public void BadCrc_BecomesSilence_NotAnException()
    {
        var pcm = MediaFixtures.Tone(4096 * 3, 500);
        var packets = MediaFixtures.ReadAll(MediaFixtures.MkaFlac(pcm));
        var dec = new FlacDecoder(new FlacEncoder().BuildHeader());
        var bad = packets[1].Data.ToArray();
        bad[bad.Length / 2] ^= 0x55;
        var buf = new short[4096];
        Assert.Equal(4096, dec.Decode(packets[0].Data.Span, buf));
        Assert.Equal(4096, dec.Decode(bad, buf));
        Assert.All(buf, s => Assert.Equal(0, s));
        Assert.Equal(1, dec.ErrorFrames);
        Assert.Equal(4096, dec.Decode(packets[2].Data.Span, buf));
        Assert.Equal(pcm[8192..12288], buf);
        // garbage and a truncated frame
        Assert.Equal(4096, dec.Decode(new byte[] { 1, 2, 3 }, buf));
        Assert.Equal(4096, dec.Decode(packets[0].Data.Span[..20], buf));
        Assert.Equal(3, dec.ErrorFrames);
    }

    // ---- frames built by a small independent encoder: LPC, stereo modes, 24 bit, wasted bits, Rice variants

    private static int[] Sig(int n, int seed, int bits, double noise = 300)
    {
        var rnd = new Random(seed);
        var amp = (1 << (bits - 2));
        return Enumerable.Range(0, n).Select(i => (int)Math.Round(amp * 0.7 * Math.Sin(i * 0.031) + amp * 0.2 * Math.Sin(i * 0.37) + noise * (rnd.NextDouble() - 0.5))).ToArray();
    }

    private static FlacDecoder Dec(int channels, int bps, int rate = 44100) => new(TestFlac.StreamInfo(rate, channels, bps));

    [Theory]
    [InlineData(1, 12, 0, 0)]
    [InlineData(2, 14, 1, 2)]
    [InlineData(4, 12, 0, 3)]
    [InlineData(8, 15, 1, 0)]
    [InlineData(12, 12, 1, 4)]
    [InlineData(32, 12, 0, 1)]
    public void Lpc_Subframes_DecodeBitExact(int order, int precision, int riceMethod, int partOrder)
    {
        var x = Sig(4096, order, 16);
        var frame = TestFlac.Frame(7, 4096, 16, 0, [x], [new TestFlac.Sub(TestFlac.Kind.Lpc, order, precision, riceMethod, partOrder)]);
        var dec = Dec(1, 16);
        var buf = new short[4096];
        Assert.Equal(4096, dec.Decode(frame, buf));
        Assert.Equal(x.Select(v => (short)v), buf);
        Assert.Equal(0, dec.ErrorFrames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FixedPredictors_And_RiceVariants(int order)
    {
        var x = Sig(1024, 20 + order, 16);
        foreach (var (method, po) in new[] { (0, 0), (0, 4), (1, 0), (1, 3) })
        {
            var frame = TestFlac.Frame(1, 1024, 16, 0, [x], [new TestFlac.Sub(TestFlac.Kind.Fixed, order, 0, method, po)]);
            var buf = new short[1024];
            Assert.Equal(1024, Dec(1, 16).Decode(frame, buf));
            Assert.Equal(x.Select(v => (short)v), buf);
        }
    }

    [Fact]
    public void RiceEscape_ConstantVerbatim_AndWastedBits()
    {
        var x = Sig(512, 3, 16);
        var buf = new short[512];
        foreach (var sub in new[]
        {
            new TestFlac.Sub(TestFlac.Kind.Fixed, 2, 0, 0, 2, Escape: true),
            new TestFlac.Sub(TestFlac.Kind.Fixed, 2, 0, 1, 1, Escape: true),
            new TestFlac.Sub(TestFlac.Kind.Verbatim),
        })
        {
            Assert.Equal(512, Dec(1, 16).Decode(TestFlac.Frame(2, 512, 16, 0, [x], [sub]), buf));
            Assert.Equal(x.Select(v => (short)v), buf);
        }
        Assert.Equal(512, Dec(1, 16).Decode(TestFlac.Frame(2, 512, 16, 0, [Enumerable.Repeat(-1234, 512).ToArray()], [new TestFlac.Sub(TestFlac.Kind.Constant)]), buf));
        Assert.All(buf, s => Assert.Equal(-1234, s));
        // wasted bits: every sample is a multiple of 8
        var w = x.Select(v => v & ~7).ToArray();
        foreach (var kind in new[] { TestFlac.Kind.Fixed, TestFlac.Kind.Lpc, TestFlac.Kind.Verbatim, TestFlac.Kind.Constant })
        {
            var sig = kind == TestFlac.Kind.Constant ? Enumerable.Repeat(-16, 512).ToArray() : w;
            Assert.Equal(512, Dec(1, 16).Decode(TestFlac.Frame(3, 512, 16, 0, [sig], [new TestFlac.Sub(kind, 3, 12, 0, 1, Wasted: 3)]), buf));
            Assert.Equal(sig.Select(v => (short)v), buf);
        }
    }

    [Theory]
    [InlineData(1)] // left/side
    [InlineData(2)] // side/right
    [InlineData(3)] // mid/side
    [InlineData(0)] // independent
    public void Stereo_Modes(int mode)
    {
        var l = Sig(2048, 1, 16);
        var r = Sig(2048, 2, 16, 3000);
        int[][] ch;
        int assign;
        switch (mode)
        {
            case 1: ch = [l, l.Zip(r, (a, b) => a - b).ToArray()]; assign = 8; break;
            case 2: ch = [l.Zip(r, (a, b) => a - b).ToArray(), r]; assign = 9; break;
            case 3: ch = [l.Zip(r, (a, b) => (a + b) >> 1).ToArray(), l.Zip(r, (a, b) => a - b).ToArray()]; assign = 10; break;
            default: ch = [l, r]; assign = 1; break;
        }
        var frame = TestFlac.Frame(4, 2048, 16, assign, ch, [new TestFlac.Sub(TestFlac.Kind.Lpc, 4, 12, 0, 3), new TestFlac.Sub(TestFlac.Kind.Fixed, 2, 0, 0, 2)]);
        var dec = Dec(2, 16);
        var buf = new short[4096];
        Assert.Equal(2048, dec.Decode(frame, buf));
        for (var i = 0; i < 2048; i++)
        {
            Assert.Equal(l[i], buf[2 * i]);
            Assert.Equal(r[i], buf[2 * i + 1]);
        }
    }

    [Fact]
    public void TwentyFourBit_Mono_And_EightBit()
    {
        var x = Sig(1024, 9, 24, 5000);
        var dec = Dec(1, 24);
        var frame = TestFlac.Frame(0, 1024, 24, 0, [x], [new TestFlac.Sub(TestFlac.Kind.Lpc, 6, 14, 1, 2)]);
        var ints = new int[1024];
        Assert.Equal(1024, dec.DecodeInt32(frame, ints));
        Assert.Equal(x, ints);
        var shorts = new short[1024];
        Assert.Equal(1024, dec.Decode(frame, shorts));
        Assert.Equal(x.Select(v => (short)(v >> 8)), shorts);

        var y = Sig(300, 4, 8, 20);
        var d8 = Dec(1, 8);
        Assert.Equal(300, d8.Decode(TestFlac.Frame(0, 300, 8, 0, [y], [new TestFlac.Sub(TestFlac.Kind.Fixed, 1, 0, 0, 0)]), shorts));
        Assert.Equal(y.Select(v => (short)(v << 8)), shorts.Take(300));
    }

    [Fact]
    public void StreamInfo_BareAndWithMagic_AreAccepted()
    {
        var si = TestFlac.StreamInfo(48000, 2, 24);
        var a = new FlacDecoder(si);
        var b = new FlacDecoder([.. "fLaC"u8.ToArray(), 0x80, 0, 0, 34, .. si]);
        Assert.Equal((48000, 2, 24), (a.SampleRate, a.Channels, a.BitsPerSample));
        Assert.Equal((48000, 2, 24), (b.SampleRate, b.Channels, b.BitsPerSample));
        Assert.Throws<InvalidDataException>(() => new FlacDecoder(new byte[10]));
    }
}

/// <summary>A small FLAC frame writer, independent of <see cref="FlacEncoder"/>, producing what that encoder never does (LPC, stereo, 5-bit Rice, escape, wasted bits, 24-bit).</summary>
internal static class TestFlac
{
    public enum Kind { Constant, Verbatim, Fixed, Lpc }

    public sealed record Sub(Kind Kind, int Order = 0, int Precision = 12, int Method = 0, int PartOrder = 0, int Wasted = 0, bool Escape = false);

    private sealed class Bits
    {
        private readonly List<byte> _b = [];
        private int _acc, _n;
        public void W(long v, int bits)
        {
            for (var i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | (int)((v >> i) & 1);
                if (++_n == 8) { _b.Add((byte)_acc); _acc = 0; _n = 0; }
            }
        }
        public void Unary(int q) { for (var i = 0; i < q; i++) { W(0, 1); } W(1, 1); }
        public void Align() { while (_n != 0) { W(0, 1); } }
        public byte[] Bytes() => _b.ToArray();
    }

    public static byte[] StreamInfo(int rate, int channels, int bps)
    {
        var b = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(b, 16);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), 4096);
        ulong v = ((ulong)rate << 44) | ((ulong)(channels - 1) << 41) | ((ulong)(bps - 1) << 36) | 123456;
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(10), v);
        return b;
    }

    public static byte[] Frame(long number, int blockSize, int bps, int assign, int[][] channels, Sub[] subs)
    {
        var bw = new Bits();
        bw.W(0x3FFE, 14); bw.W(0, 1); bw.W(0, 1);
        bw.W(7, 4);          // block size: 16-bit (n-1) follows
        bw.W(0, 4);          // sample rate from STREAMINFO
        bw.W(assign == 0 ? channels.Length - 1 : assign, 4);
        bw.W(bps switch { 8 => 1, 12 => 2, 16 => 4, 20 => 5, 24 => 6, _ => throw new ArgumentException() }, 3);
        bw.W(0, 1);
        bw.W(number, 8);     // frame numbers < 128 in these tests
        bw.W(blockSize - 1, 16);
        var head = bw.Bytes();
        bw.W(FlacEncoder.Crc8(head, head.Length), 8);
        for (var c = 0; c < channels.Length; c++)
        {
            var side = (assign == 8 && c == 1) || (assign == 9 && c == 0) || (assign == 10 && c == 1);
            Subframe(bw, channels[c], bps + (side ? 1 : 0), subs[Math.Min(c, subs.Length - 1)]);
        }
        bw.Align();
        var body = bw.Bytes();
        bw.W(FlacEncoder.Crc16(body, body.Length), 16);
        return bw.Bytes();
    }

    private static void Subframe(Bits bw, int[] xs, int bps, Sub s)
    {
        bw.W(0, 1);
        var type = s.Kind switch
        {
            Kind.Constant => 0,
            Kind.Verbatim => 1,
            Kind.Fixed => 8 + s.Order,
            _ => 32 + s.Order - 1,
        };
        bw.W(type, 6);
        if (s.Wasted > 0) { bw.W(1, 1); bw.Unary(s.Wasted - 1); } else { bw.W(0, 1); }
        var x = xs.Select(v => v >> s.Wasted).ToArray();
        bps -= s.Wasted;
        var n = x.Length;
        switch (s.Kind)
        {
            case Kind.Constant:
                bw.W(x[0], bps);
                return;
            case Kind.Verbatim:
                foreach (var v in x) { bw.W(v, bps); }
                return;
        }
        var order = s.Order;
        for (var i = 0; i < order; i++) { bw.W(x[i], bps); }
        var res = new int[n - order];
        if (s.Kind == Kind.Fixed)
        {
            for (var i = order; i < n; i++)
            {
                long p = order switch
                {
                    0 => 0,
                    1 => x[i - 1],
                    2 => 2L * x[i - 1] - x[i - 2],
                    3 => 3L * x[i - 1] - 3L * x[i - 2] + x[i - 3],
                    _ => 4L * x[i - 1] - 6L * x[i - 2] + 4L * x[i - 3] - x[i - 4],
                };
                res[i - order] = (int)(x[i] - p);
            }
        }
        else
        {
            var (coef, shift) = LpcCoefficients(x, order, s.Precision);
            bw.W(s.Precision - 1, 4);
            bw.W(shift, 5);
            foreach (var c in coef) { bw.W(c, s.Precision); }
            for (var i = order; i < n; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++) { sum += (long)coef[j] * x[i - 1 - j]; }
                res[i - order] = (int)(x[i] - (sum >> shift));
            }
        }
        Residual(bw, res, n, order, s);
    }

    private static void Residual(Bits bw, int[] res, int n, int order, Sub s)
    {
        bw.W(s.Method, 2);
        bw.W(s.PartOrder, 4);
        var pb = s.Method == 0 ? 4 : 5;
        var parts = 1 << s.PartOrder;
        var idx = 0;
        for (var p = 0; p < parts; p++)
        {
            var count = (n >> s.PartOrder) - (p == 0 ? order : 0);
            var part = res.AsSpan(idx, count).ToArray();
            idx += count;
            if (s.Escape && p == 0)
            {
                var bits = part.Length == 0 ? 0 : part.Max(v => SignedBits(v));
                bw.W((1 << pb) - 1, pb);
                bw.W(bits, 5);
                foreach (var v in part) { bw.W(v, bits); }
                continue;
            }
            var best = 0; long bestBits = long.MaxValue;
            for (var k = 0; k < (1 << pb) - 1; k++)
            {
                long bits = 0;
                foreach (var v in part) { bits += (((uint)((v << 1) ^ (v >> 31))) >> k) + 1 + k; }
                if (bits < bestBits) { bestBits = bits; best = k; }
            }
            bw.W(best, pb);
            foreach (var v in part)
            {
                var u = (uint)((v << 1) ^ (v >> 31));
                bw.Unary((int)(u >> best));
                bw.W(u & ((1u << best) - 1), best);
            }
        }
    }

    private static int SignedBits(int v)
    {
        var bits = 1;
        while (bits < 32 && (v < -(1L << (bits - 1)) || v > (1L << (bits - 1)) - 1)) { bits++; }
        return bits;
    }

    /// <summary>Levinson-Durbin on the block's autocorrelation, quantized to <paramref name="precision"/> bits.</summary>
    private static (int[] Coef, int Shift) LpcCoefficients(int[] x, int order, int precision)
    {
        var r = new double[order + 1];
        for (var lag = 0; lag <= order; lag++)
        {
            double sum = 0;
            for (var i = lag; i < x.Length; i++) { sum += (double)x[i] * x[i - lag]; }
            r[lag] = sum;
        }
        var a = new double[order + 1];
        var err = r[0] * (1 + 1e-9) + 1e-9;
        for (var i = 1; i <= order; i++)
        {
            double acc = r[i];
            for (var j = 1; j < i; j++) { acc -= a[j] * r[i - j]; }
            var k = acc / err;
            var next = (double[])a.Clone();
            next[i] = k;
            for (var j = 1; j < i; j++) { next[j] = a[j] - k * a[i - j]; }
            a = next;
            err *= 1 - k * k;
            if (err <= 0) { break; }
        }
        var max = Math.Max(a.Skip(1).Select(Math.Abs).DefaultIfEmpty(0).Max(), 1e-9);
        var shift = Math.Clamp(precision - 1 - (int)Math.Ceiling(Math.Log2(max)), 0, 15);
        var limit = (1 << (precision - 1)) - 1;
        var q = new int[order];
        for (var j = 0; j < order; j++) { q[j] = Math.Clamp((int)Math.Round(a[j + 1] * (1 << shift)), -limit - 1, limit); }
        return (q, shift);
    }
}

public class AudioTrackDecoderTests
{
    private static short[] ReadAll(AudioTrackDecoder d, int chunk = 1000)
    {
        var all = new List<short>();
        var buf = new short[chunk];
        int n;
        while ((n = d.Read(buf)) > 0) { all.AddRange(buf.AsSpan(0, n)); }
        return all.ToArray();
    }

    [Fact]
    public void Wav44k_IsPassedThroughExactly_AndSeeksSampleAccurately()
    {
        using var tmp = new TempFiles();
        var pcm = MediaFixtures.Ramp(100_000);
        using var d = AudioTrackDecoder.Open(tmp.Write("a.wav", MediaFixtures.Wav(pcm, 44100, 1)));
        Assert.True(d.CanPlay);
        Assert.Equal(pcm, ReadAll(d, 777));
        d.Seek(TimeSpan.FromSeconds(1));
        var buf = new short[100];
        Assert.Equal(100, d.Read(buf));
        Assert.Equal(pcm[44100..44200], buf);
        Assert.Equal(TimeSpan.FromTicks(44200L * 10_000_000 / 44100), d.Position);
        d.Seek(TimeSpan.FromSeconds(100)); // past the end
        Assert.Equal(0, d.Read(buf));
    }

    [Fact]
    public void Resample48kTo44k_LengthFrequencyAndAccuracy()
    {
        using var tmp = new TempFiles();
        foreach (var freq in new[] { 1000.0, 5000.0, 15000.0 })
        {
            var src = MediaFixtures.Tone(48000 * 2, freq, 48000);
            using var d = AudioTrackDecoder.Open(tmp.Write($"t{freq}.wav", MediaFixtures.Wav(src, 48000, 1)));
            var got = ReadAll(d, 1234);
            Assert.InRange(got.Length, 88200 - 2, 88200 + 2);
            // steady state against the ideal tone sampled at 44.1 kHz
            double worst = 0;
            for (var n = 400; n < got.Length - 400; n++)
            {
                var ideal = 10000 * Math.Sin(2 * Math.PI * freq * n / 44100);
                worst = Math.Max(worst, Math.Abs(got[n] - ideal));
            }
            Assert.True(worst < 150, $"{freq} Hz: worst error {worst}");
            // frequency from zero crossings
            var crossings = 0;
            for (var n = 1; n < got.Length; n++) { if ((got[n - 1] < 0) != (got[n] < 0)) { crossings++; } }
            Assert.InRange(crossings, 2 * freq * 2 - 4, 2 * freq * 2 + 4);
        }
    }

    [Fact]
    public void Resample_SeekAndPositionStayConsistent()
    {
        using var tmp = new TempFiles();
        var src = MediaFixtures.Tone(48000 * 3, 1000, 48000);
        using var d = AudioTrackDecoder.Open(tmp.Write("t.wav", MediaFixtures.Wav(src, 48000, 1)));
        d.Seek(TimeSpan.FromSeconds(1.5));
        var buf = new short[500];
        Assert.Equal(500, d.Read(buf));
        for (var i = 0; i < 500; i++)
        {
            var n = 66150 + i;
            Assert.InRange(buf[i], 10000 * Math.Sin(2 * Math.PI * 1000 * n / 44100) - 150, 10000 * Math.Sin(2 * Math.PI * 1000 * n / 44100) + 150);
        }
        // 22.05 kHz upsampling
        var low = MediaFixtures.Tone(22050, 1000, 22050);
        using var up = AudioTrackDecoder.Open(tmp.Write("l.wav", MediaFixtures.Wav(low, 22050, 1)));
        var got = ReadAll(up);
        Assert.InRange(got.Length, 44100 - 2, 44100 + 2);
        for (var n = 200; n < got.Length - 200; n += 7) { Assert.InRange(got[n], 10000 * Math.Sin(2 * Math.PI * 1000 * n / 44100) - 200, 10000 * Math.Sin(2 * Math.PI * 1000 * n / 44100) + 200); }
    }

    [Fact]
    public void StereoIsAveragedToMono()
    {
        using var tmp = new TempFiles();
        var inter = new short[2000];
        for (var i = 0; i < 1000; i++) { inter[2 * i] = 1000; inter[2 * i + 1] = 3001; }
        using var d = AudioTrackDecoder.Open(tmp.Write("s.wav", MediaFixtures.Wav(inter, 44100, 2)));
        var got = ReadAll(d);
        Assert.Equal(1000, got.Length);
        Assert.All(got, s => Assert.Equal(2000, s));
    }

    [Fact]
    public void FlacInMatroska_DecodesAndSeeks()
    {
        using var tmp = new TempFiles();
        var pcm = MediaFixtures.Ramp(44100 * 4);
        using var d = AudioTrackDecoder.Open(tmp.Write("a.mka", MediaFixtures.MkaFlac(pcm)));
        Assert.True(d.CanPlay);
        Assert.Equal(pcm, ReadAll(d, 4000));
        d.Seek(TimeSpan.FromSeconds(2.0));
        var buf = new short[64];
        Assert.Equal(64, d.Read(buf));
        // block timestamps are millisecond-rounded: within ~22 samples
        var start = Array.IndexOf(pcm, buf[0], 88000);
        Assert.InRange(start, 88200 - 25, 88200 + 25);
        Assert.Equal(pcm.AsSpan(start, 64).ToArray(), buf);
    }

    [Fact]
    public void LaterStart_GivesLeadingSilence_AndGapsBecomeSilence()
    {
        using var tmp = new TempFiles();
        var pcm = MediaFixtures.Tone(44100 * 2, 300, amp: 12000);
        // the first block starts 1.0 s into the file
        using (var d = AudioTrackDecoder.Open(tmp.Write("late.mka", MediaFixtures.MkaFlac(pcm, startTicks: 10_000_000))))
        {
            var got = ReadAll(d, 3000);
            Assert.InRange(got.Length, 44100 * 3 - 30, 44100 * 3 + 30);
            Assert.All(got.Take(44100 - 25), s => Assert.Equal(0, s));
            Assert.Contains(got.Skip(44100 + 30).Take(500), s => Math.Abs(s) > 5000);
            // seeking into the silence
            d.Seek(TimeSpan.FromSeconds(0.5));
            var b = new short[100];
            Assert.Equal(100, d.Read(b));
            Assert.All(b, s => Assert.Equal(0, s));
            // and past the start of the audio
            d.Seek(TimeSpan.FromSeconds(1.5));
            Assert.Equal(100, d.Read(b));
            Assert.Contains(b, s => Math.Abs(s) > 1000);
        }
        // a missing FLAC block (block 2 = 0.186 s..0.279 s) becomes silence of the same length; the rest stays aligned
        using var g = AudioTrackDecoder.Open(tmp.Write("gap.mka", MediaFixtures.MkaFlac(pcm, skipBlock: 2)));
        var all = ReadAll(g);
        Assert.InRange(all.Length, pcm.Length - 30, pcm.Length + 30);
        Assert.All(all.Skip(8192 + 30).Take(4096 - 60), s => Assert.Equal(0, s));
        // Matroska timecodes are 1 ms: the block after the gap is placed within +-0.5 ms (22 samples) of its true position
        Assert.Contains(Enumerable.Range(-25, 51), o => Enumerable.Range(100, 300).All(i => all[12288 + i + o] == pcm[12288 + i]));
    }

    [Fact]
    public void Aac_ThroughFactory_PrimingOffsetAndSeek()
    {
        using var tmp = new TempFiles();
        var factory = new FakeAudioDecoderFactory();
        // no priming: sample n carries packet n/1024
        using (var d = AudioTrackDecoder.Open(tmp.Write("a.m4a", MediaFixtures.M4a(100)), factory: factory))
        {
            Assert.True(d.CanPlay);
            var all = ReadAll(d, 5000);
            Assert.Equal(100 * 1024, all.Length);
            Assert.Equal(0, all[0]);
            Assert.Equal(99, all[^1]);
            Assert.Equal(50, all[50 * 1024 + 10]);

            // seek accuracy: 1.0 s is sample 44100 = packet 43
            d.Seek(TimeSpan.FromSeconds(1));
            var buf = new short[2048];
            Assert.Equal(2048, d.Read(buf));
            Assert.Equal((short)43, buf[0]);
            Assert.Equal((short)43, buf[(43 + 1) * 1024 - 44100 - 1]);
            Assert.Equal((short)44, buf[(43 + 1) * 1024 - 44100]);
            Assert.True(factory.Resets >= 1, "decoder is reset on seek");
            // a seek right at the start (no pre-roll possible)
            d.Seek(TimeSpan.Zero);
            Assert.Equal(2048, d.Read(buf));
            Assert.Equal((short)0, buf[0]);
            Assert.Equal((short)1, buf[1024]);
        }
        // 1024 samples of encoder delay in the edit list are skipped: output starts at packet 1
        using (var d = AudioTrackDecoder.Open(tmp.Write("p.m4a", MediaFixtures.M4a(100, priming: 1024)), factory: factory))
        {
            var all = ReadAll(d, 5000);
            Assert.Equal(99 * 1024, all.Length);
            Assert.Equal(1, all[0]);
            d.Seek(TimeSpan.FromSeconds(1));
            var buf = new short[16];
            d.Read(buf);
            Assert.Equal((short)(44100 / 1024 + 1), buf[0]); // timeline sample 44100 = decoded sample 45124 = packet 44
        }
    }

    [Fact]
    public void Mp4VideoFile_AudioTrackSelection_StartsWithLaterAudio()
    {
        using var tmp = new TempFiles();
        var (file, _) = MediaFixtures.Mp4(3, audioTracks: 2);
        var factory = new FakeAudioDecoderFactory();
        var path = tmp.Write("v.mp4", file);
        using var d = AudioTrackDecoder.Open(path, trackIndex: 2, factory);
        Assert.Equal("Mic 1", d.Track.Name);
        var all = ReadAll(d, 4096);
        Assert.InRange(all.Length, 3 * 44100 - 1024, 3 * 44100 + 1024);
        Assert.Equal(0, all[0]);
        Assert.Equal(1, factory.Created);
        using var first = AudioTrackDecoder.Open(path, -1, factory);
        Assert.Equal(2, factory.Created); // every open creates its own decoder
        Assert.Equal("Mic 0", first.Track.Name);
        Assert.Throws<ArgumentException>(() => AudioTrackDecoder.Open(path, trackIndex: 0, factory)); // the video track
    }

    [Fact]
    public void UndecodableTrack_ReportsInsteadOfThrowing()
    {
        using var tmp = new TempFiles();
        var path = tmp.Write("a.m4a", MediaFixtures.M4a(10));

        using (var none = AudioTrackDecoder.Open(path))
        {
            Assert.False(none.CanPlay);
            Assert.Contains("AAC", none.Problem);
            Assert.Equal(0, none.Read(new short[100]));
            none.Seek(TimeSpan.FromSeconds(1));
            Assert.Equal(0, none.Read(new short[100]));
            Assert.True(none.Duration > TimeSpan.Zero);
        }
        var factory = new FakeAudioDecoderFactory { Supported = false };
        using (var no = AudioTrackDecoder.Open(path, factory: factory))
        {
            Assert.False(no.CanPlay);
            Assert.NotNull(no.Problem);
            Assert.Equal(0, factory.Created);
        }
        Assert.False(AudioTrackDecoder.CanDecode(new MediaTrack(0, MediaKind.Audio, "aac", null, null, 44100, 1, 16, 0, 0, 0, TimeSpan.Zero), null));
        Assert.True(AudioTrackDecoder.CanDecode(new MediaTrack(0, MediaKind.Audio, "flac", null, null, 44100, 1, 16, 0, 0, 0, TimeSpan.Zero), null));
        Assert.False(AudioTrackDecoder.CanDecode(new MediaTrack(0, MediaKind.Audio, "opus", null, null, 48000, 2, 16, 0, 0, 0, TimeSpan.Zero), null));

        // a decoder factory that throws on Create is reported too
        var throwing = new ThrowingFactory();
        using var t = AudioTrackDecoder.Open(path, factory: throwing);
        Assert.False(t.CanPlay);
        Assert.Contains("boom", t.Problem);
    }

    private sealed class ThrowingFactory : IAudioDecoderFactory
    {
        public bool CanDecode(string codec) => true;
        public IAudioDecoder Create(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Mp3ThroughFactory_Works()
    {
        Assert.True(LameMp3Encoder.IsAvailable);
        var ms = new MemoryStream();
        var pcm = MediaFixtures.Tone(44100, 440);
        using (var enc = new LameMp3Encoder(44100, 128_000))
        {
            enc.Encode(pcm, ms);
            enc.Finish(ms);
        }
        using var tmp = new TempFiles();
        using var d = AudioTrackDecoder.Open(tmp.Write("a.mp3", ms.ToArray()), factory: new Mp3Fake());
        var all = ReadAll(d, 3000);
        Assert.InRange(all.Length, 44100 - 1152, 44100 + 1152 * 2);
    }

    private sealed class Mp3Fake : IAudioDecoderFactory
    {
        public bool CanDecode(string codec) => codec == "mp3";
        public IAudioDecoder Create(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate) => new Dec(sampleRate, channels);

        private sealed class Dec(int rate, int ch) : IAudioDecoder
        {
            public int SampleRate => rate;
            public int Channels => ch;
            public void Decode(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<short>> output) => output(new short[1152 * ch]);
            public void Drain(Action<ReadOnlySpan<short>> output) { }
            public void Reset() { }
            public void Dispose() { }
        }
    }
}

internal static class TinyMp4
{
    public static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }

    public static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. U32((uint)(body.Length + 8)), .. Encoding.ASCII.GetBytes(type), .. body];
    }

    /// <summary>A trak with mdhd (timescale, duration), hdlr (<paramref name="handler"/>) and the given stbl children.</summary>
    public static byte[] Trak(string handler, uint timescale, uint duration, params byte[][] stbl) =>
        Box("trak", Box("mdia",
            Box("mdhd", new byte[4], U32(0), U32(0), U32(timescale), U32(duration), new byte[4]),
            Box("hdlr", new byte[4], U32(0), Encoding.ASCII.GetBytes(handler), new byte[12], [0]),
            Box("minf", Box("stbl", stbl))));

    public static byte[] File(params byte[][] moovChildren) =>
        [.. Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(0)), .. Box("moov", moovChildren), .. Box("mdat", new byte[64])];
}

public class Mp4WriterRobustnessTests
{
    private sealed class FlushCounter : MemoryStream
    {
        public int Flushes;
        public override void Flush() { Flushes++; base.Flush(); }
    }

    [Fact]
    public void MoovAndEveryFragment_AreFlushedImmediately()
    {
        var ms = new FlushCounter();
        using var w = new Mp4Writer(ms, audioOnly: true, leaveOpen: true);
        var t = w.AddAudio("Mic", 44100, 1, [0x12, 0x08]);
        for (var k = 0; k < 108; k++) { w.WriteSample(t, 0, BitConverter.GetBytes(k)); } // 2.5 s: moov + fragment 1 at 1 s, fragment 2 at 2 s
        Assert.True(ms.Length > 0);
        Assert.True(ms.Flushes >= 3, $"flushes before Dispose: {ms.Flushes}");
    }

    [Fact]
    public void VideoWithoutKeyFrame_IsDroppedAfterBoundedWait_AudioReachesTheDisk()
    {
        var ms = new MemoryStream();
        var w = new Mp4Writer(ms, audioOnly: false, leaveOpen: true);
        var v = w.AddVideo("Video", 640, 480);
        var a = w.AddAudio("Mic", 44100, 1, [0x12, 0x08]);
        for (var k = 0; k < 100; k++) { w.WriteSample(a, 0, BitConverter.GetBytes(k)); } // 2.3 s: still waiting
        Assert.Equal(0, ms.Length);
        Assert.False(w.VideoDropped);
        for (var k = 100; k < 140; k++) { w.WriteSample(a, 0, BitConverter.GetBytes(k)); } // 3.2 s: gives up
        Assert.True(w.VideoDropped);
        Assert.True(v.Dropped);
        Assert.True(ms.Length > 0, "moov and the buffered audio are on disk");
        v.AvcC = MediaFixtures.Avcc;
        w.WriteSample(v, 0, new byte[] { 1, 2 }, true); // too late: ignored
        for (var k = 140; k < 200; k++) { w.WriteSample(a, 0, BitConverter.GetBytes(k)); }
        w.Dispose();

        var file = ms.ToArray();
        var moov = Mp4Box.ParseFile(file).Path("moov");
        var trak = Assert.Single(moov.FindAll("trak"));
        Assert.Equal(2u, trak.Path("tkhd").U32(12)); // the audio track keeps its id
        Assert.Single(moov.Path("mvex").FindAll("trex"));
        using var r = new Mp4Reader(new MemoryStream(file));
        Assert.Equal(MediaKind.Audio, Assert.Single(r.Tracks).Kind);
        Assert.Equal(200, MediaFixtures.ReadAll(r).Count);
    }

    [Fact]
    public void VideoNeverConfigured_DropsItAtDispose()
    {
        var ms = new MemoryStream();
        var w = new Mp4Writer(ms, audioOnly: false, leaveOpen: true);
        w.AddVideo("Video", 640, 480);
        var a = w.AddAudio("Mic", 44100, 1, [0x12, 0x08]);
        for (var k = 0; k < 10; k++) { w.WriteSample(a, 0, BitConverter.GetBytes(k)); }
        w.Dispose();
        Assert.True(w.VideoDropped);
        using var r = new Mp4Reader(new MemoryStream(ms.ToArray()));
        Assert.Equal(MediaKind.Audio, Assert.Single(r.Tracks).Kind); // no empty avcC that players reject
        Assert.Equal(10, MediaFixtures.ReadAll(r).Count);
    }

    [Fact]
    public void CrashImage_HasNoHalfPatchedEditList_ButOurReaderStillHonoursOffsetAndPriming()
    {
        var ms = new MemoryStream();
        var w = new Mp4Writer(ms, audioOnly: false, leaveOpen: true);
        var v = w.AddVideo("Video", 640, 480);
        v.AvcC = MediaFixtures.Avcc;
        var a = w.AddAudio("Mic", 44100, 1, [0x12, 0x08]);
        a.PrimingSamples = 1024;
        var audio = 0;
        for (var i = 0; i < 75; i++)
        {
            w.WriteSample(v, 5_000_000 + i * 10_000_000L / 30, new byte[] { (byte)i, 1, 2 }, i % 30 == 0);
            while (audio * 1024.0 / 44100 <= (i + 1) / 30.0) { w.WriteSample(a, 0, BitConverter.GetBytes(audio++)); }
        }
        var crashed = ms.ToArray();
        w.Dispose();
        var finished = ms.ToArray();

        foreach (var trak in Mp4Box.ParseFile(crashed).Path("moov").FindAll("trak")) { Assert.Null(trak.Find("edts")); }
        Assert.All(Mp4Box.ParseFile(finished).Path("moov").FindAll("trak"), t => Assert.NotNull(t.Find("edts")));

        long First(byte[] f, int track) => MediaFixtures.ReadAll(f).First(p => p.Track == track).Timestamp;
        Assert.Equal(First(finished, 0), First(crashed, 0));
        Assert.Equal(5_000_000, First(crashed, 0));
        Assert.Equal(First(finished, 1), First(crashed, 1));
        Assert.True(First(crashed, 1) < 0);
    }
}

public class ReaderFuzzTests
{
    private static byte[] Mutate(byte[] seed, Random rnd)
    {
        var d = (byte[])seed.Clone();
        var kind = rnd.Next(6);
        if (kind == 5) { return d[..rnd.Next(1, d.Length)]; }
        var edits = rnd.Next(1, 5);
        for (var e = 0; e < edits; e++)
        {
            // most of the structure (headers, tables) sits at the start
            var at = rnd.Next(100) < 70 ? rnd.Next(Math.Min(d.Length, 3000)) : rnd.Next(d.Length);
            switch (kind)
            {
                case 0: d[at] ^= (byte)(1 << rnd.Next(8)); break;
                case 1: d[at] = (byte)rnd.Next(256); break;
                case 2: for (var i = 0; i < 4 && at + i < d.Length; i++) { d[at + i] = 0xFF; } break;
                case 3: if (at + 3 < d.Length) { d[at] = 0x7F; d[at + 1] = d[at + 2] = d[at + 3] = 0xFF; } break;
                default: for (var i = 0; i < 4 && at + i < d.Length; i++) { d[at + i] = 0; } break;
            }
        }
        return d;
    }

    internal static void Exercise(byte[] data, Func<Stream, IMediaReader> open, string what)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            using var r = open(new MemoryStream(data));
            for (var n = 0; n < 5000 && r.TryRead(out _); n++) { }
            r.Seek(TimeSpan.FromSeconds(1));
            for (var n = 0; n < 5000 && r.TryRead(out _); n++) { }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { }
        catch (Exception ex) { Assert.Fail($"{what}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 96L << 20, $"{what}: allocated {allocated >> 20} MB for a {data.Length} byte file");
    }

    private static void Fuzz(string name, byte[] seed, Func<Stream, IMediaReader> open, int cases)
    {
        var rnd = new Random(name.Length * 7919 + 12345);
        Exercise(seed, open, name + " (original)");
        // PLURAL_FUZZ_SCALE=10 for a longer local run
        cases *= int.TryParse(Environment.GetEnvironmentVariable("PLURAL_FUZZ_SCALE"), out var scale) && scale > 0 ? scale : 1;
        for (var i = 0; i < cases; i++) { Exercise(Mutate(seed, rnd), open, $"{name} case {i}"); }
    }

    internal static IMediaReader Mp4(Stream s) => new Mp4Reader(s);
    private static IMediaReader Mkv(Stream s) => new MatroskaReader(s);

    [Fact]
    public void Mp4Reader_SurvivesMutatedWriterOutput()
    {
        Fuzz("mp4", MediaFixtures.Mp4(2, videoStart: 3_000_000, audioTracks: 2, priming: 1024).File, Mp4, 400);
        Fuzz("mp4-crash", MediaFixtures.Mp4(2, crash: true).File, Mp4, 200);
        Fuzz("m4a", MediaFixtures.M4a(100, priming: 1024), Mp4, 200);
    }

    [Fact]
    public void MatroskaReader_SurvivesMutatedWriterOutput()
    {
        Fuzz("mkv", MediaFixtures.Mkv(2).File, Mkv, 400);
        Fuzz("mkv-crash", MediaFixtures.Mkv(2, crash: true).File, Mkv, 200);
        Fuzz("mka", MediaFixtures.MkaFlac(MediaFixtures.Tone(44100, 440)), Mkv, 200);
        Fuzz("mka-pcm", MediaFixtures.MkaPcm(MediaFixtures.Tone(20000, 440)), Mkv, 100);
    }

    [Fact]
    public void ProgressiveMp4_WithAbsurdTables_IsBoundedByTheFile()
    {
        var stbl = new[]
        {
            TinyMp4.Box("stsd", TinyMp4.U32(0), TinyMp4.U32(0)),
            TinyMp4.Box("stts", TinyMp4.U32(0), TinyMp4.U32(1), TinyMp4.U32(uint.MaxValue), TinyMp4.U32(1024)),
            TinyMp4.Box("stsc", TinyMp4.U32(0), TinyMp4.U32(1), TinyMp4.U32(1), TinyMp4.U32(uint.MaxValue), TinyMp4.U32(1)),
            TinyMp4.Box("stsz", TinyMp4.U32(0), TinyMp4.U32(1), TinyMp4.U32(uint.MaxValue)), // 4 billion samples of 1 byte
            TinyMp4.Box("stco", TinyMp4.U32(0), TinyMp4.U32(1), TinyMp4.U32(8)),
        };
        Exercise(TinyMp4.File(TinyMp4.Trak("soun", 44100, 1000, stbl)), Mp4, "huge fixed-size tables");

        // sizes of 4 GB each, a chunk count that exceeds the payload, and a short stsz
        var stbl2 = new[]
        {
            TinyMp4.Box("stsd", TinyMp4.U32(0), TinyMp4.U32(0)),
            TinyMp4.Box("stsz", TinyMp4.U32(0), TinyMp4.U32(0), TinyMp4.U32(uint.MaxValue), TinyMp4.U32(uint.MaxValue)),
            TinyMp4.Box("stco", TinyMp4.U32(0), TinyMp4.U32(uint.MaxValue), TinyMp4.U32(8)),
            TinyMp4.Box("stsc", TinyMp4.U32(0), TinyMp4.U32(2), TinyMp4.U32(1), TinyMp4.U32(1), TinyMp4.U32(1)),
        };
        Exercise(TinyMp4.File(TinyMp4.Trak("soun", 44100, 1000, stbl2)), Mp4, "huge sizes");
        Exercise(TinyMp4.File(TinyMp4.Box("mvex", TinyMp4.Box("mehd", [1]), TinyMp4.Box("trex", [0, 0]))), Mp4, "short mehd/trex");
        Exercise(TinyMp4.File(TinyMp4.Box("trak", TinyMp4.Box("tkhd"), TinyMp4.Box("mdia", TinyMp4.Box("mdhd", [0]), TinyMp4.Box("hdlr")))), Mp4, "empty boxes");
    }
}

public class AudioTrackDecoderRobustnessTests
{
    private static short[] ReadAll(AudioTrackDecoder d, int chunk = 4000)
    {
        var all = new List<short>();
        var buf = new short[chunk];
        int n;
        while ((n = d.Read(buf)) > 0) { all.AddRange(buf.AsSpan(0, n)); }
        return all.ToArray();
    }

    private sealed class FailingFactory(int failAt, bool failDrain = false) : IAudioDecoderFactory
    {
        public bool CanDecode(string codec) => true;
        public IAudioDecoder Create(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate) => new Dec(sampleRate, channels, failAt, failDrain);

        private sealed class Dec(int rate, int ch, int failAt, bool failDrain) : IAudioDecoder
        {
            private int _n;
            public int SampleRate => rate;
            public int Channels => ch;
            public void Decode(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<short>> output)
            {
                if (_n++ == failAt) { throw new InvalidDataException("corrupt packet"); }
                output(new short[1024 * ch]);
            }
            public void Drain(Action<ReadOnlySpan<short>> output) { if (failDrain) { throw new InvalidOperationException("drain failed"); } }
            public void Reset() { _n = 0; }
            public void Dispose() { }
        }
    }

    [Fact]
    public void DecoderException_EndsTheTrackWithAProblem_InsteadOfThrowing()
    {
        using var tmp = new TempFiles();
        var path = tmp.Write("a.m4a", MediaFixtures.M4a(100));
        using (var d = AudioTrackDecoder.Open(path, factory: new FailingFactory(failAt: 5)))
        {
            Assert.Equal(5 * 1024, ReadAll(d).Length); // everything before the corrupt packet
            Assert.Contains("corrupt packet", d.Problem);
            Assert.True(d.CanPlay);
            Assert.Equal(0, d.Read(new short[100]));
            d.Seek(TimeSpan.Zero); // a seek back to the good part works again
            Assert.Equal(1024, d.Read(new short[1024]));
        }
        using (var d = AudioTrackDecoder.Open(path, factory: new FailingFactory(failAt: -1, failDrain: true)))
        {
            Assert.Equal(100 * 1024, ReadAll(d).Length);
            Assert.Contains("drain failed", d.Problem);
        }
    }

    [Fact]
    public void CorruptFlacBlocks_NeverThrowAtPlayTime()
    {
        using var tmp = new TempFiles();
        var good = MediaFixtures.MkaFlac(MediaFixtures.Tone(44100, 440));
        var rnd = new Random(5);
        for (var i = 0; i < 40; i++)
        {
            var bad = (byte[])good.Clone();
            for (var k = 0; k < 6; k++) { bad[rnd.Next(bad.Length / 4, bad.Length)] ^= (byte)rnd.Next(1, 256); }
            AudioTrackDecoder d;
            try { d = AudioTrackDecoder.Open(tmp.Write($"c{i}.mka", bad)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { continue; } // header damage is reported when opening
            using (d) { _ = ReadAll(d); }
        }
    }

    private static byte[] PcmMkaWithBlocksAt(params long[] startTicks)
    {
        var ms = new MemoryStream();
        using (var w = new MatroskaWriter(ms, "t", leaveOpen: true))
        {
            var t = w.AddAudio("Pcm", "A_PCM/INT/LIT", 44100, 1, 16);
            foreach (var at in startTicks)
            {
                var b = new byte[4096 * 2];
                for (var k = 0; k < 4096; k++) { BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2 * k), (short)(1000 + (int)(at / 10_000_000))); }
                w.WriteBlock(t, at, b);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public void LongTimestampGap_IsNotMaterialized_ButKeepsTheTimeline()
    {
        using var tmp = new TempFiles();
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        using var d = AudioTrackDecoder.Open(tmp.Write("gap.mka", PcmMkaWithBlocksAt(0, 300_000_000))); // second block at 30 s
        var all = ReadAll(d, 44100);
        Assert.InRange(all.Length, 30 * 44100 + 4096 - 50, 30 * 44100 + 4096 + 50);
        Assert.Equal(1000, all[100]);
        Assert.Equal(0, all[15 * 44100]);
        Assert.Equal(1030, all[^100]);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocBefore < 64L << 20);
    }

    [Fact]
    public void ImplausibleTimestampJump_StopsWithAProblem()
    {
        using var tmp = new TempFiles();
        using var d = AudioTrackDecoder.Open(tmp.Write("jump.mka", PcmMkaWithBlocksAt(0, 7200L * 10_000_000))); // 2 hours later
        var all = ReadAll(d);
        Assert.Equal(4096, all.Length);
        Assert.Contains("timestamp", d.Problem, StringComparison.OrdinalIgnoreCase);
    }
}

public class Mp3ReaderOpenCostTests
{
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Reads;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; return inner.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { Reads++; return inner.Read(buffer); }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void CbrFileWithoutXing_IsEstimatedFromItsSize_NotScannedAtOpen()
    {
        // 2000 CBR frames (128 kbit/s, 44.1 kHz, mono) = 52 s, no Xing/Info/VBRI header
        var bytes = new List<byte>();
        double acc = 0;
        for (var i = 0; i < 2000; i++)
        {
            acc += 144.0 * 128000 / 44100;
            var len = (int)acc; acc -= len;
            bytes.AddRange(new byte[] { 0xFF, 0xFB, (byte)(0x90 | (len == 418 ? 2 : 0)), 0xC0 });
            bytes.AddRange(new byte[len - 4]);
        }
        var counting = new CountingStream(new MemoryStream(bytes.ToArray()));
        using var r = new Mp3Reader(counting);
        Assert.True(counting.Reads < 100, $"reads at open: {counting.Reads}");
        Assert.InRange(r.Duration.TotalSeconds, 52.2 - 0.1, 52.2 + 0.1);
        // seeking still works (scans on demand)
        r.Seek(TimeSpan.FromSeconds(10));
        Assert.True(r.TryRead(out var p));
        Assert.InRange(p.Timestamp, 10_000_000 * 10 - 270_000, 10_000_000 * 10);
    }
}
