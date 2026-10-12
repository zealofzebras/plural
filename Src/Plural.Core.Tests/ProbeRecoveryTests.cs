using System.Buffers.Binary;
using Plural.Core.Audio;
using Plural.Core.Library;
using Plural.Core.Media;
using Plural.Core.Recording;

namespace Plural.Core.Tests;

public class MediaProbeTests
{
    private static byte[] Mp3(int seconds)
    {
        Assert.True(LameMp3Encoder.IsAvailable, "mp3lame native library missing");
        var ms = new MemoryStream();
        using (var enc = new LameMp3Encoder(44100, 128_000))
        {
            enc.Encode(MediaFixtures.Tone(44100 * seconds, 440), ms);
            enc.Finish(ms);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Wav_Mp3_M4a_Mka_AreProbedWithTheRealDuration()
    {
        using var tmp = new TempFiles();

        var wav = Assert.Single(MediaProbe.Probe(tmp.Write("a.wav", MediaFixtures.Wav(MediaFixtures.Tone(88200, 440), 44100, 1))));
        Assert.Equal((TrackKinds.Audio, "pcm_s16le", 44100, 1, 0), (wav.Kind, wav.Codec, wav.SampleRate, wav.Channels, wav.TrackIndex));
        Assert.Equal(2000, wav.DurationMs, 1);

        var mp3 = Assert.Single(MediaProbe.Probe(tmp.Write("a.mp3", Mp3(2))));
        Assert.Equal(("mp3", 44100), (mp3.Codec, mp3.SampleRate));
        Assert.InRange(mp3.DurationMs, 1900, 2150);

        var m4a = Assert.Single(MediaProbe.Probe(tmp.Write("a.m4a", MediaFixtures.M4a(100))));
        Assert.Equal((TrackKinds.Audio, "aac", 44100, 1), (m4a.Kind, m4a.Codec, m4a.SampleRate, m4a.Channels));
        Assert.Equal(100 * 1024 * 1000.0 / 44100, m4a.DurationMs, 1);
        Assert.Equal("Mic", m4a.Name);

        var mka = Assert.Single(MediaProbe.Probe(tmp.Write("a.mka", MediaFixtures.MkaFlac(MediaFixtures.Tone(88200, 440), "Lav"))));
        Assert.Equal((TrackKinds.Audio, "flac", 44100, "Lav"), (mka.Kind, mka.Codec, mka.SampleRate, mka.Name));
        Assert.InRange(mka.DurationMs, 1900, 2100);
    }

    [Fact]
    public void Mkv_And_Mp4_HaveVideoAndAudioTracksInContainerOrder()
    {
        using var tmp = new TempFiles();

        var mkv = MediaProbe.Probe(tmp.Write("a.mkv", MediaFixtures.Mkv(2, rotation: 90).File));
        Assert.Equal([0, 1, 2], mkv.Select(t => t.TrackIndex));
        Assert.Equal([TrackKinds.Video, TrackKinds.Audio, TrackKinds.Audio], mkv.Select(t => t.Kind));
        Assert.Equal(("h264", 1280, 720, 90, "Video"), (mkv[0].Codec, mkv[0].Width, mkv[0].Height, mkv[0].Rotation, mkv[0].Name));
        Assert.Equal(["Mic A", "Mic B"], mkv.Skip(1).Select(t => t.Name));
        Assert.Equal(("flac", 44100, 1), (mkv[1].Codec, mkv[1].SampleRate, mkv[1].Channels));
        Assert.All(mkv, t => Assert.InRange(t.DurationMs, 1800, 2100));
        Assert.Equal((0, 0, 0), (mkv[1].Width, mkv[1].Height, mkv[1].Rotation));
        Assert.Equal(0, mkv[0].SampleRate);

        var mp4 = MediaProbe.Probe(tmp.Write("a.mp4", MediaFixtures.Mp4(2, rotation: 270, audioTracks: 2).File));
        Assert.Equal([TrackKinds.Video, TrackKinds.Audio, TrackKinds.Audio], mp4.Select(t => t.Kind));
        Assert.Equal(("h264", 1920, 1080, 270), (mp4[0].Codec, mp4[0].Width, mp4[0].Height, mp4[0].Rotation));
        Assert.Equal(("aac", 44100), (mp4[1].Codec, mp4[1].SampleRate));
        Assert.Equal(["Mic 0", "Mic 1"], mp4.Skip(1).Select(t => t.Name));
        Assert.All(mp4, t => Assert.InRange(t.DurationMs, 1900, 2200));
    }

    [Fact]
    public void CrashedMkv_UnknownSizes_NoDuration_Truncated_GetsARealDuration()
    {
        using var tmp = new TempFiles();
        var (full, _) = MediaFixtures.Mkv(4, crash: true);
        var cut = full[..(full.Length * 6 / 10)];
        var tracks = MediaProbe.Probe(tmp.Write("crash.mkv", cut));
        Assert.Equal(3, tracks.Count);
        Assert.Equal(TrackKinds.Video, tracks[0].Kind);
        Assert.Equal((1280, 720, 90), (tracks[0].Width, tracks[0].Height, tracks[0].Rotation));
        // not 0, and shorter than the 4 s that were recorded
        Assert.All(tracks, t => Assert.InRange(t.DurationMs, 1500, 3900));
        var whole = MediaProbe.Probe(tmp.Write("whole.mkv", full));
        Assert.InRange(whole[0].DurationMs, 3800, 4100);
    }

    [Fact]
    public void CrashedFragmentedMp4_And_M4a_Truncated_GetARealDuration()
    {
        using var tmp = new TempFiles();
        var m4a = MediaFixtures.M4a(300, crash: true); // 6.97 s
        var tracks = MediaProbe.Probe(tmp.Write("crash.m4a", m4a[..(m4a.Length * 6 / 10)]));
        var t = Assert.Single(tracks);
        Assert.Equal("aac", t.Codec);
        Assert.InRange(t.DurationMs, 2500, 6900);

        var (mp4, _) = MediaFixtures.Mp4(4, crash: true);
        var video = MediaProbe.Probe(tmp.Write("crash.mp4", mp4[..(mp4.Length * 6 / 10)]));
        Assert.Equal(2, video.Count);
        Assert.Equal((TrackKinds.Video, 1920, 1080, 90), (video[0].Kind, video[0].Width, video[0].Height, video[0].Rotation));
        Assert.All(video, v => Assert.InRange(v.DurationMs, 1000, 3900));
    }

    [Fact]
    public void Unreadable_Throws_AndTheLibraryFallsBackToTheBasicProbe()
    {
        using var tmp = new TempFiles();
        var junk = tmp.Write("junk.wav", [1, 2, 3, 4]);
        Assert.ThrowsAny<Exception>(() => MediaProbe.Probe(junk));

        var root = Path.Combine(Path.GetTempPath(), "plural_probe_" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "20261012_101010");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "junk.wav"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(folder, "mic.mka"), MediaFixtures.MkaFlac(MediaFixtures.Tone(44100, 440), "Mic"));
            var t = new TakeLibrary(root).Load(folder)!;
            Assert.Equal(2, t.Tracks.Count);
            Assert.Equal("junk", t.Tracks.Single(x => x.File == "junk.wav").Name); // stub
            Assert.Equal("Mic", t.Tracks.Single(x => x.File == "mic.mka").Name);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void TakeLibrary_UsesTheRealProbeByDefault_EvenForACrashedMkv()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_probe2_" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(root, "20261012_101010");
            Directory.CreateDirectory(folder);
            var (full, _) = MediaFixtures.Mkv(3, crash: true);
            File.WriteAllBytes(Path.Combine(folder, "20261012_101010.mkv"), full[..(full.Length * 7 / 10)]);
            var t = new TakeLibrary(root).Load(folder)!;
            Assert.Equal(RecordingFormat.Mkv, t.Format);
            Assert.True(t.HasVideo);
            Assert.Equal([0, 1, 2], t.Tracks.Select(x => x.TrackIndex));
            Assert.Equal("20261012_101010.mkv#2", t.Tracks[2].Id);
            Assert.InRange(t.Duration.TotalSeconds, 1.0, 3.0); // the Basic probe would say 0
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }
}

public class PeakServiceTests
{
    private static TakeInfo MakeTake(string root, Action<string> fill, string name = "20261012_101010")
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        fill(folder);
        return new TakeLibrary(root).Load(folder)!;
    }

    private static void With(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_peakjob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private sealed class Progress(List<string> into) : IProgress<string>
    {
        public void Report(string value) { lock (into) { into.Add(value); } }
    }

    [Fact]
    public async Task OldWavAndMkaTracks_GetPeaksMatchingTheComputedOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_peakjob_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pcm = MediaFixtures.Tone(44100 * 2 + 77, 330);
            var take = MakeTake(root, f =>
            {
                File.WriteAllBytes(Path.Combine(f, "a.wav"), MediaFixtures.Wav(pcm, 44100, 1));
                File.WriteAllBytes(Path.Combine(f, "b.mka"), MediaFixtures.MkaFlac(pcm, "B"));
            });
            Assert.All(take.Tracks, t => Assert.True(PeakService.NeedsPeaks(take, t)));
            var done = new List<string>();
            await PeakService.EnsurePeaksAsync(take, null, new Progress(done));
            Assert.Equal(["a.wav#0", "b.mka#0"], done.OrderBy(x => x, StringComparer.Ordinal));
            var (l0, l1) = PeakBuilder.Build(pcm);
            foreach (var t in take.Tracks)
            {
                var r = PeakReader.Open(TrackInfo.PeakPath(take.Folder, t.Id))!;
                Assert.Equal(pcm.Length, r.SampleCount);
                var min = new short[l0.Length / 2];
                var max = new short[l0.Length / 2];
                r.GetRange(0, 256, min, max);
                for (var i = 0; i < min.Length; i++)
                {
                    Assert.Equal(l0[2 * i], min[i]);
                    Assert.Equal(l0[2 * i + 1], max[i]);
                }
                Assert.False(PeakService.NeedsPeaks(take, t));
            }
            Assert.Empty(Directory.GetFiles(Path.Combine(take.Folder, TakeSidecar.PeaksFolder), "*.tmp"));

            // second run has nothing to do
            done.Clear();
            await PeakService.EnsurePeaksAsync(take, null, new Progress(done));
            Assert.Empty(done);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task AacTrack_NeedsADecoder_AndIsSkippedWithoutOne()
    {
        using var tmp = new TempFiles();
        var root = Path.Combine(Path.GetTempPath(), "plural_peakjob_" + Guid.NewGuid().ToString("N"));
        try
        {
            var take = MakeTake(root, f => File.WriteAllBytes(Path.Combine(f, "m.m4a"), MediaFixtures.M4a(100)));
            var done = new List<string>();
            await PeakService.EnsurePeaksAsync(take, null, new Progress(done));
            Assert.Empty(done);
            Assert.True(PeakService.NeedsPeaks(take, take.Tracks[0]));

            var factory = new FakeAudioDecoderFactory();
            await PeakService.EnsurePeaksAsync(take, factory, new Progress(done));
            Assert.Equal(["m.m4a#0"], done);
            var r = PeakReader.Open(TrackInfo.PeakPath(take.Folder, "m.m4a#0"))!;
            Assert.Equal(100 * 1024, r.SampleCount);
            var min = new short[1];
            var max = new short[1];
            r.GetRange(99 * 1024, 1024, min, max);
            Assert.Equal((99, 99), (min[0], max[0])); // the fake decoder: packet k = 1024 samples of k

            // a decoder that cannot be created is skipped, not thrown
            File.Delete(TrackInfo.PeakPath(take.Folder, "m.m4a#0"));
            done.Clear();
            await PeakService.EnsurePeaksAsync(take, new FakeAudioDecoderFactory { Supported = false }, new Progress(done));
            Assert.Empty(done);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task PeaksFileMuchShorterThanTheTrack_IsRebuilt_AShortGapIsNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_peakjob_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pcm = MediaFixtures.Tone(44100 * 4, 330);
            var take = MakeTake(root, f => File.WriteAllBytes(Path.Combine(f, "a.wav"), MediaFixtures.Wav(pcm, 44100, 1)));
            var track = take.Tracks[0];
            var path = TrackInfo.PeakPath(take.Folder, track.Id);

            using (var w = new PeakWriter(path, 44100)) { w.Write(pcm.AsSpan(0, 44100)); } // 1 s of 4 s
            Assert.True(PeakService.NeedsPeaks(take, track));
            var done = new List<string>();
            await PeakService.EnsurePeaksAsync(take, null, new Progress(done));
            Assert.Equal([track.Id], done);
            Assert.Equal(pcm.Length, PeakReader.Open(path)!.SampleCount);

            using (var w = new PeakWriter(path, 44100)) { w.Write(pcm.AsSpan(0, 44100 * 4 - 20_000)); } // 0.45 s short: fine
            Assert.False(PeakService.NeedsPeaks(take, track));

            // a crashed recording: peaks file without its final header
            using (var w = new PeakWriter(path, 44100)) { w.Write(pcm.AsSpan(0, 44100 * 2)); }
            var bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 0);
            File.WriteAllBytes(path, bytes);
            Assert.True(PeakService.NeedsPeaks(take, track));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task VideoTracks_RecordingTakes_AndMissingFiles_AreLeftAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_peakjob_" + Guid.NewGuid().ToString("N"));
        try
        {
            var take = MakeTake(root, f =>
            {
                File.WriteAllBytes(Path.Combine(f, "v.mkv"), MediaFixtures.Mkv(1).File);
                File.WriteAllBytes(Path.Combine(f, "a.wav"), MediaFixtures.Wav(MediaFixtures.Tone(44100, 330), 44100, 1));
            });
            var video = take.Tracks.Single(t => t.IsVideo);
            Assert.False(PeakService.NeedsPeaks(take, video));
            Assert.False(PeakService.NeedsPeaks(take with { Status = TakeStatus.Recording }, take.Tracks.First(t => !t.IsVideo)));

            File.Delete(Path.Combine(take.Folder, "a.wav"));
            var done = new List<string>();
            await PeakService.EnsurePeaksAsync(take with { Status = TakeStatus.Recording }, null, new Progress(done));
            await PeakService.EnsurePeaksAsync(take, null, new Progress(done));
            // the mkv's two audio tracks are processed, the deleted wav is not
            Assert.DoesNotContain("a.wav#0", done);
            Assert.Equal(2, done.Count);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task Cancelling_StopsTheJob_AndLeavesNoTempFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_peakjob_" + Guid.NewGuid().ToString("N"));
        try
        {
            var pcm = MediaFixtures.Tone(44100 * 60, 330);
            var take = MakeTake(root, f => File.WriteAllBytes(Path.Combine(f, "a.wav"), MediaFixtures.Wav(pcm, 44100, 1)));

            using var already = new CancellationTokenSource();
            already.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PeakService.EnsurePeaksAsync(take, null, null, already.Token));

            // cancel from the progress callback of the first (of two) tracks
            File.WriteAllBytes(Path.Combine(take.Folder, "b.wav"), MediaFixtures.Wav(pcm, 44100, 1));
            File.Delete(Path.Combine(take.Folder, TakeSidecar.FileName)); // probe again
            var two = new TakeLibrary(root).Load(take.Folder)!;
            Assert.Equal(2, two.Tracks.Count);
            using var cts = new CancellationTokenSource();
            var done = new List<string>();
            var progress = new Progress(done);
            var cancelOnFirst = new CancelingProgress(progress, cts);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PeakService.EnsurePeaksAsync(two, null, cancelOnFirst, cts.Token));
            Assert.Single(done);
            Assert.Empty(Directory.GetFiles(Path.Combine(take.Folder, TakeSidecar.PeaksFolder), "*.tmp"));
            Assert.Single(Directory.GetFiles(Path.Combine(take.Folder, TakeSidecar.PeaksFolder), "*.peaks"));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    private sealed class CancelingProgress(IProgress<string> inner, CancellationTokenSource cts) : IProgress<string>
    {
        public void Report(string value)
        {
            inner.Report(value);
            cts.Cancel();
        }
    }
}

public class RecoveryTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "plural_recover_" + Guid.NewGuid().ToString("N"));

    private static void With(Action<string> test)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    /// <summary>A take folder that was being recorded: the files, and a sidecar saying so with unknown (0) durations.</summary>
    private static string CrashedTake(string root, string name, params (string File, byte[] Data, TrackInfo[] Tracks)[] files)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        var tracks = new List<TrackInfo>();
        foreach (var (file, data, t) in files)
        {
            File.WriteAllBytes(Path.Combine(folder, file), data);
            foreach (var x in t)
            {
                x.File = file;
                x.Id = TrackInfo.MakeId(file, x.TrackIndex);
                x.DurationMs = 0;
                tracks.Add(x);
            }
        }
        TakeSidecar.Update(folder, s =>
        {
            s.Status = TakeStatus.Recording;
            s.Tracks = tracks;
        });
        return folder;
    }

    private static byte[] WavCrashed(int samples, uint dataSize = 0, int extraBytes = 0)
    {
        var wav = MediaFixtures.Wav(MediaFixtures.Tone(samples, 440), 44100, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), dataSize);
        return [.. wav, .. new byte[extraBytes]];
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(0u, 1)]
    [InlineData(0xFFFFFFFFu, 0)]
    [InlineData(5_000_000u, 0)]
    public void Wav_HeaderWithCrashSizes_IsPatchedToTheRealLength(uint stale, int partialFrameBytes)
    {
        With(root =>
        {
            var folder = CrashedTake(root, "20261012_101010", ("mic.wav", WavCrashed(44100, stale, partialFrameBytes),
                [new TrackInfo { Kind = TrackKinds.Audio, Codec = "pcm_s16le", SampleRate = 44100, Channels = 1 }]));
            var path = Path.Combine(folder, "mic.wav");
            Assert.True(MediaRepair.TryRepair(path));
            var b = File.ReadAllBytes(path);
            Assert.Equal(44 + 88200, b.Length); // partial frame cut
            Assert.Equal(88200u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40)));
            Assert.Equal((uint)(b.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
            Assert.False(MediaRepair.TryRepair(path)); // a good header is left alone
            using var r = MediaReader.Open(path);
            Assert.Equal(1.0, r.Duration.TotalSeconds, 3);
        });
    }

    [Fact]
    public void Wav_ValidFileWithTrailingChunk_IsNotTouched()
    {
        With(root =>
        {
            var wav = MediaFixtures.Wav(MediaFixtures.Tone(4410, 440), 44100, 1);
            var withList = wav.Concat("LIST"u8.ToArray()).Concat(new byte[] { 4, 0, 0, 0, 1, 2, 3, 4 }).ToArray();
            var path = Path.Combine(root, "a.wav");
            File.WriteAllBytes(path, withList);
            Assert.False(MediaRepair.TryRepair(path));
            Assert.Equal(withList, File.ReadAllBytes(path));
            File.WriteAllBytes(Path.Combine(root, "junk.wav"), [1, 2, 3]);
            Assert.False(MediaRepair.TryRepair(Path.Combine(root, "junk.wav")));
        });
    }

    private static long ReadSegmentSizeField(byte[] mkv)
    {
        // EBML header, then Segment ID (4 bytes) and its 8-byte size
        var hdrLen = 4 + 1 + (int)mkv[4] - 0x80; // 4-byte ID, 1-byte size (< 127), body
        Assert.Equal(new byte[] { 0x18, 0x53, 0x80, 0x67 }, mkv[hdrLen..(hdrLen + 4)]);
        return mkv[hdrLen + 4] == 0x01 && mkv[(hdrLen + 5)..(hdrLen + 12)].All(x => x == 0xFF)
            ? -1
            : (long)(BinaryPrimitives.ReadUInt64BigEndian(mkv.AsSpan(hdrLen + 4)) & 0x00FFFFFFFFFFFFFF);
    }

    [Fact]
    public void CrashedMkv_GetsSegmentSizeAndDuration_AndStaysReadableIdentically()
    {
        With(root =>
        {
            var (full, _) = MediaFixtures.Mkv(4, crash: true);
            var cut = full[..(full.Length * 6 / 10)];
            var folder = CrashedTake(root, "20261012_101010", ("t.mkv", cut, [
                new TrackInfo { Kind = TrackKinds.Video, TrackIndex = 0, Codec = "h264" },
                new TrackInfo { Kind = TrackKinds.Audio, TrackIndex = 1, Codec = "flac" },
                new TrackInfo { Kind = TrackKinds.Audio, TrackIndex = 2, Codec = "flac" }]));
            var path = Path.Combine(folder, "t.mkv");
            Assert.Equal(-1, ReadSegmentSizeField(cut));

            List<MediaPacket> before;
            using (var r = MediaReader.Open(path)) { before = MediaFixtures.ReadAll(r); }

            var lib = new TakeLibrary(root);
            Assert.Equal([folder], lib.RecoverInterrupted());

            var after = File.ReadAllBytes(path);
            Assert.Equal(cut.Length, after.Length); // nothing appended or cut
            // only two fixed-width fields differ
            var diff = Enumerable.Range(0, cut.Length).Count(i => cut[i] != after[i]);
            Assert.InRange(diff, 1, 16);
            Assert.True(ReadSegmentSizeField(after) > 0);

            List<MediaPacket> now;
            double infoDuration;
            using (var r = MediaReader.Open(path))
            {
                now = MediaFixtures.ReadAll(r);
                infoDuration = r.Duration.TotalSeconds;
            }
            Assert.Equal(before.Count, now.Count);
            Assert.Equal(before.Select(p => (p.Track, p.Timestamp, p.Data.Length)), now.Select(p => (p.Track, p.Timestamp, p.Data.Length)));
            Assert.InRange(infoDuration, 1.5, 3.9);

            // Info now holds the duration: a reader no longer needs to scan for it
            var sc = TakeSidecar.Load(folder)!;
            Assert.Equal(TakeStatus.Recovered, sc.Status);
            Assert.All(sc.Tracks, t => Assert.InRange(t.DurationMs, 1500, 3900));
            Assert.Equal(infoDuration * 1000, sc.Tracks[0].DurationMs, 0);

            // running it again changes nothing
            Assert.Empty(lib.RecoverInterrupted());
            Assert.Equal(after, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void CrashedMka_And_FragmentedMp4_AndM4a_GetDurations_Mp4FilesAreNotRewritten()
    {
        With(root =>
        {
            var mka = MediaFixtures.MkaFlac(MediaFixtures.Tone(44100 * 5, 440), "Mic", crash: true);
            var mkaCut = mka[..(mka.Length * 8 / 10)];
            var m4a = MediaFixtures.M4a(400, crash: true); // 9.3 s
            var m4aCut = m4a[..(m4a.Length * 7 / 10)];
            var (mp4, _) = MediaFixtures.Mp4(4, crash: true);
            var mp4Cut = mp4[..(mp4.Length * 7 / 10)];
            var folder = CrashedTake(root, "20261012_101010",
                ("a.mka", mkaCut, [new TrackInfo { Kind = TrackKinds.Audio, Codec = "flac" }]),
                ("b.m4a", m4aCut, [new TrackInfo { Kind = TrackKinds.Audio, Codec = "aac" }]),
                ("c.mp4", mp4Cut, [new TrackInfo { Kind = TrackKinds.Video, Codec = "h264" }, new TrackInfo { Kind = TrackKinds.Audio, TrackIndex = 1, Codec = "aac" }]));

            new TakeLibrary(root).RecoverInterrupted();
            Assert.Equal(m4aCut, File.ReadAllBytes(Path.Combine(folder, "b.m4a")));
            Assert.Equal(mp4Cut, File.ReadAllBytes(Path.Combine(folder, "c.mp4")));
            var t = TakeSidecar.Load(folder)!.Tracks;
            Assert.InRange(t.Single(x => x.File == "a.mka").DurationMs, 2000, 5000);
            Assert.InRange(t.Single(x => x.File == "b.m4a").DurationMs, 3000, 9300);
            Assert.All(t.Where(x => x.File == "c.mp4"), x => Assert.InRange(x.DurationMs, 1000, 3900));
        });
    }

    [Fact]
    public void Matroska_NotOurLayout_OrAlreadyFinished_IsLeftAlone()
    {
        With(root =>
        {
            var finished = MediaFixtures.MkaFlac(MediaFixtures.Tone(44100, 440));
            var p = Path.Combine(root, "done.mka");
            File.WriteAllBytes(p, finished);
            Assert.False(MediaRepair.TryRepair(p));
            Assert.Equal(finished, File.ReadAllBytes(p));

            var garbage = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray();
            var g = Path.Combine(root, "garbage.mkv");
            File.WriteAllBytes(g, garbage);
            Assert.False(MediaRepair.TryRepair(g));
            Assert.Equal(garbage, File.ReadAllBytes(g));
        });
    }

    [Fact]
    public void Recovery_FillsDurationsFromPeaks_WhenTheFileCannotBeProbed_AndSkipsCompleteTakes()
    {
        With(root =>
        {
            var folder = CrashedTake(root, "20261012_101010", ("gone.wav", [1, 2, 3, 4],
                [new TrackInfo { Kind = TrackKinds.Audio, Codec = "pcm_s16le" }]));
            using (var w = new PeakWriter(TrackInfo.PeakPath(folder, "gone.wav#0"), 44100)) { w.Write(new short[44100 * 3]); }
            var done = Path.Combine(root, "20261012_111111");
            Directory.CreateDirectory(done);
            File.WriteAllBytes(Path.Combine(done, "a.wav"), WavCrashed(100));
            TakeSidecar.Update(done, s => { s.Status = TakeStatus.Complete; s.Tracks = [new TrackInfo { Id = "a.wav#0", File = "a.wav" }]; });

            var recovered = new TakeLibrary(root).RecoverInterrupted();
            Assert.Equal([folder], recovered);
            Assert.InRange(TakeSidecar.Load(folder)!.Tracks[0].DurationMs, 2999, 3001);
            Assert.Equal(WavCrashed(100), File.ReadAllBytes(Path.Combine(done, "a.wav"))); // complete takes are not touched
        });
    }
}
