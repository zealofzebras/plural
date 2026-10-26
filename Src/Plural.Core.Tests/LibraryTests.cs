using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Plural.Core.Audio;
using Plural.Core.Library;
using Plural.Core.Media;
using Plural.Core.Platform;
using Plural.Core.Recording;
using Plural.Core.Remote;
using Plural.Core.Session;

namespace Plural.Core.Tests;

[Collection("timing")]
public class PeakTests
{
    private static short[] Signal(int n, int seed = 1)
    {
        var rnd = new Random(seed);
        var s = new short[n];
        for (var i = 0; i < n; i++) { s[i] = (short)(rnd.Next(-20000, 20000) * Math.Sin(i * 0.0007)); }
        return s;
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "plural_peaks_" + Guid.NewGuid().ToString("N") + ".peaks");

    [Fact]
    public void Builder_ComputesBlocksAndDecimation()
    {
        var pcm = Signal(256 * 64 * 2 + 300);
        var (l0, l1) = PeakBuilder.Build(pcm);
        Assert.Equal(2 * (2 * 64 + 2), l0.Length); // 130 blocks (last one partial)
        Assert.Equal(2 * 3, l1.Length);
        Assert.Equal(pcm.AsSpan(0, 256).ToArray().Min(), l0[0]);
        Assert.Equal(pcm.AsSpan(0, 256).ToArray().Max(), l0[1]);
        Assert.Equal(pcm.AsSpan(256 * 129, 44).ToArray().Min(), l0[^2]);
        Assert.Equal(pcm.AsSpan(0, 256 * 64).ToArray().Max(), l1[1]);
    }

    [Fact]
    public void Writer_FileMatchesBuilder_InAnyChunking()
    {
        var pcm = Signal(100_003);
        var path = Temp();
        try
        {
            using (var w = new PeakWriter(path, 44100))
            {
                for (var i = 0; i < pcm.Length;)
                {
                    var n = Math.Min(pcm.Length - i, 1 + (i * 7) % 3000);
                    w.Write(pcm.AsSpan(i, n));
                    i += n;
                }
                w.Complete();
            }
            var r = PeakReader.Open(path)!;
            Assert.Equal(pcm.Length, r.SampleCount);
            Assert.Equal(44100, r.SampleRate);
            var (l0, _) = PeakBuilder.Build(pcm);
            var min = new short[l0.Length / 2];
            var max = new short[l0.Length / 2];
            Assert.Equal(min.Length, r.GetRange(0, 256, min, max));
            for (var i = 0; i < min.Length; i++)
            {
                Assert.Equal(l0[2 * i], min[i]);
                Assert.Equal(l0[2 * i + 1], max[i]);
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(100.0)]
    [InlineData(256.0)]
    [InlineData(1000.0)]
    [InlineData(16384.0)]
    [InlineData(50000.0)]
    public void Reader_MatchesBruteForceAtZoomLevels(double spp)
    {
        var pcm = Signal(300_000, 3);
        var path = Temp();
        try
        {
            using (var w = new PeakWriter(path, 44100)) { w.Write(pcm); }
            var r = PeakReader.Open(path)!;
            const long start = 7_777;
            var cols = 40;
            var min = new short[cols];
            var max = new short[cols];
            r.GetRange(start, spp, min, max);
            var block = spp >= 16384 ? 16384 : 256; // the reader answers in whole blocks of the level it used
            for (var x = 0; x < cols; x++)
            {
                var s0 = start + (long)Math.Floor(x * spp);
                var s1 = start + (long)Math.Ceiling((x + 1) * spp) - 1;
                if (s1 < s0) { s1 = s0; }
                var from = (int)(s0 / block * block);
                var to = (int)Math.Min(pcm.Length, (s1 / block + 1) * block);
                if (from >= pcm.Length) { Assert.Equal(0, min[x]); continue; }
                var slice = pcm.AsSpan(from, to - from).ToArray();
                Assert.Equal(slice.Min(), min[x]);
                Assert.Equal(slice.Max(), max[x]);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_PastTheEnd_ReturnsEmptyColumns()
    {
        var path = Temp();
        try
        {
            using (var w = new PeakWriter(path, 44100)) { w.Write(Signal(1000)); }
            var r = PeakReader.Open(path)!;
            var min = new short[4];
            var max = new short[4];
            Assert.Equal(0, r.GetRange(1_000_000, 256, min, max));
            Assert.All(max, v => Assert.Equal(0, v));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CrashedFile_NoHeaderCount_NoLevel1_IsStillReadable()
    {
        var pcm = Signal(256 * 200 + 100);
        var path = Temp();
        try
        {
            using (var w = new PeakWriter(path, 44100)) { w.Write(pcm); }
            var good = File.ReadAllBytes(path);
            var (l0, _) = PeakBuilder.Build(pcm);
            // keep header + whole level-0 blocks only, header count stale (0), and cut mid-pair
            var crashed = good.AsSpan(0, 24 + 4 * 150 + 2).ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(crashed.AsSpan(16), 0);
            var r = PeakReader.Parse(crashed)!;
            Assert.Equal(150 * 256, r.SampleCount);
            var min = new short[150];
            var max = new short[150];
            Assert.Equal(150, r.GetRange(0, 256, min, max));
            for (var i = 0; i < 150; i++)
            {
                Assert.Equal(l0[2 * i], min[i]);
                Assert.Equal(l0[2 * i + 1], max[i]);
            }
            // zoomed out: level 1 is rebuilt from level 0
            var a = new short[1];
            var b = new short[1];
            r.GetRange(0, 50000, a, b);
            Assert.Equal(pcm.AsSpan(0, 150 * 256).ToArray().Max(), b[0]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TruncatedCompleteFile_FallsBackToLevel0()
    {
        var pcm = Signal(256 * 64 * 3);
        var path = Temp();
        try
        {
            using (var w = new PeakWriter(path, 44100)) { w.Write(pcm); }
            var good = File.ReadAllBytes(path);
            var cut = good.AsSpan(0, good.Length - 6).ToArray(); // lost the end of level 1
            var r = PeakReader.Parse(cut)!;
            Assert.Equal(pcm.Length, r.SampleCount);
            var a = new short[3];
            var b = new short[3];
            Assert.Equal(3, r.GetRange(0, 16384, a, b));
            Assert.Equal(pcm.AsSpan(2 * 16384, 16384).ToArray().Max(), b[2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reader_RejectsGarbage()
    {
        Assert.Null(PeakReader.Parse(new byte[100]));
        Assert.Null(PeakReader.Open(Temp()));
    }

    [Fact]
    public void Generator_BuildsTheSameFileAsTheWriter()
    {
        var pcm = Signal(50_000);
        var path = Temp();
        try
        {
            var pos = 0;
            PeakGenerator.Generate(buf =>
            {
                var n = Math.Min(buf.Length, Math.Min(pcm.Length - pos, 5000));
                pcm.AsSpan(pos, n).CopyTo(buf);
                pos += n;
                return n;
            }, path, 22050);
            var r = PeakReader.Open(path)!;
            Assert.Equal(pcm.Length, r.SampleCount);
            Assert.Equal(22050, r.SampleRate);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }
}

public class PosterTests
{
    [Fact]
    public void Rotate_TurnsClockwise()
    {
        // 3x2 image, pixel value = 10*y + x in the blue channel
        var src = new byte[3 * 2 * 4];
        for (var y = 0; y < 2; y++) { for (var x = 0; x < 3; x++) { src[(y * 3 + x) * 4] = (byte)(10 * y + x); } }
        var (r90, w, h) = PosterWriter.Rotate(src, 3, 2, 12, 90);
        Assert.Equal((2, 3), (w, h));
        // top row of the result = left column of the source, bottom to top
        Assert.Equal([10, 0], new[] { r90[0], r90[4] });
        Assert.Equal([12, 2], new[] { r90[(2 * 2 + 0) * 4], r90[(2 * 2 + 1) * 4] });
        var (r270, _, _) = PosterWriter.Rotate(src, 3, 2, 12, 270);
        Assert.Equal([2, 12], new[] { r270[0], r270[4] });
        var (r180, w2, h2) = PosterWriter.Rotate(src, 3, 2, 12, 180);
        Assert.Equal((3, 2), (w2, h2));
        Assert.Equal(12, r180[0]);
    }

    public static (int W, int H, byte[] Rgb) DecodePng(byte[] png)
    {
        Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        var p = 8;
        int w = 0, h = 0;
        var idat = new MemoryStream();
        while (p < png.Length)
        {
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p));
            var type = Encoding.ASCII.GetString(png, p + 4, 4);
            var data = png.AsSpan(p + 8, len);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p + 8 + len));
            Assert.Equal(Crc(png.AsSpan(p + 4, 4 + len)), crc);
            if (type == "IHDR") { w = (int)BinaryPrimitives.ReadUInt32BigEndian(data); h = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]); }
            if (type == "IDAT") { idat.Write(data); }
            p += 12 + len;
        }
        idat.Position = 0;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) { z.CopyTo(raw); }
        var bytes = raw.ToArray();
        Assert.Equal(h * (1 + w * 3), bytes.Length);
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            Assert.Equal(0, bytes[y * (1 + w * 3)]);
            Array.Copy(bytes, y * (1 + w * 3) + 1, rgb, y * w * 3, w * 3);
        }
        return (w, h, rgb);
    }

    private static uint Crc(ReadOnlySpan<byte> d)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in d)
        {
            c ^= b;
            for (var k = 0; k < 8; k++) { c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        }
        return ~c;
    }

    [Fact]
    public void EncodePng_RoundTrips()
    {
        var bgra = new byte[4 * 3 * 4];
        for (var i = 0; i < bgra.Length; i++) { bgra[i] = (byte)(i * 7); }
        var (w, h, rgb) = DecodePng(PosterWriter.EncodePng(bgra, 4, 3));
        Assert.Equal((4, 3), (w, h));
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(bgra[4 * i + 2], rgb[3 * i]);
            Assert.Equal(bgra[4 * i + 1], rgb[3 * i + 1]);
            Assert.Equal(bgra[4 * i], rgb[3 * i + 2]);
        }
    }
}

public class LibraryTests
{
    [Fact]
    public void RemoteOnlyPendingTake_AppearsWithoutTracks()
    {
        var root = NewRoot();
        var folder = Path.Combine(root, "20261017_120000");
        try
        {
            TakeManifest.Update(folder, manifest =>
            {
                manifest.TakeId = "pending-take";
                manifest.Device("phone", "Phone").Status = "pending";
            });
            var take = Assert.Single(new TakeLibrary(root).Scan());
            Assert.Empty(take.Tracks);
            Assert.Equal(TakeStatus.Pending, take.Status);
            Assert.Contains("Phone", take.Devices);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("pending", TakeStatus.Pending)]
    [InlineData("failed", TakeStatus.Pending)]
    [InlineData("syncing", TakeStatus.Syncing)]
    public void MixedRemoteTake_PreservesSyncedTracksAndPendingNamesWithoutPersistingPartialTracks(string remoteStatus, string expectedStatus)
    {
        var root = NewRoot();
        var folder = Path.Combine(root, "20261017_120000");
        try
        {
            MakeWav(Path.Combine(folder, "Phone"), "audio");
            MakeWav(Path.Combine(folder, "Tablet"), "partial");
            TakeManifest.Update(folder, manifest =>
            {
                var phone = manifest.Device("phone", "Phone");
                phone.Folder = "Phone";
                phone.Status = "synced";
                var tablet = manifest.Device("tablet", "Tablet");
                tablet.Folder = "Tablet";
                tablet.Status = remoteStatus;
            });
            var take = new TakeLibrary(root).Load(folder)!;
            Assert.Equal(expectedStatus, take.Status);
            Assert.Contains("Tablet", take.Devices);
            Assert.Contains(take.Tracks, track => track.Device == "Phone");
            Assert.Equal("Phone", Assert.Single(TakeSidecar.Load(folder)!.Tracks).Device);
            TakeManifest.Update(folder, manifest => manifest.Device("tablet", "Tablet").Status = "synced");
            var complete = new TakeLibrary(root).Load(folder)!;
            Assert.Equal(TakeStatus.Complete, complete.Status);
            Assert.Equal(2, TakeSidecar.Load(folder)!.Tracks.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "plural_lib_" + Guid.NewGuid().ToString("N"));

    private static string MakeWav(string folder, string name, double seconds = 1, short amp = 5000)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".wav");
        using var w = new WavWriter(path, 44100, 1);
        var pcm = new short[(int)(44100 * seconds)];
        for (var i = 0; i < pcm.Length; i++) { pcm[i] = (short)(amp * Math.Sin(i * 0.05)); }
        w.Write(pcm);
        return path;
    }

    private static void With(Action<string> test)
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    // ------------------------------------------------------------------ sidecar

    [Fact]
    public void Sidecar_RoundTrip_KeepsUnknownFields()
    {
        With(root =>
        {
            const string json = """
                {
                  "version": 7, "title": "Interview", "created": "2026-10-12T10:10:10+00:00", "status": "complete",
                  "futureThing": { "a": [1, 2, 3] },
                  "tracks": [ { "id": "a.wav#0", "kind": "audio", "file": "a.wav", "mood": "calm", "durationMs": 1000 } ],
                  "pauses": [ { "atMs": 500, "why": "coffee" } ],
                  "playback": { "a.wav#0": { "gainDb": -3, "mute": true, "solo": false } }
                }
                """;
            File.WriteAllText(Path.Combine(root, TakeSidecar.FileName), json);
            TakeSidecar.Update(root, s => s.Notes = "hello");
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, TakeSidecar.FileName)));
            var e = doc.RootElement;
            Assert.Equal(3, e.GetProperty("futureThing").GetProperty("a").GetArrayLength());
            Assert.Equal("calm", e.GetProperty("tracks")[0].GetProperty("mood").GetString());
            Assert.Equal("coffee", e.GetProperty("pauses")[0].GetProperty("why").GetString());
            Assert.Equal("hello", e.GetProperty("notes").GetString());
            Assert.Equal("Interview", e.GetProperty("title").GetString());
            Assert.Equal(-3, e.GetProperty("playback").GetProperty("a.wav#0").GetProperty("gainDb").GetDouble());
            Assert.Equal(1000, e.GetProperty("tracks")[0].GetProperty("durationMs").GetDouble());
            Assert.False(File.Exists(Path.Combine(root, TakeSidecar.FileName + ".tmp")));
            var s = TakeSidecar.Load(root)!;
            Assert.Equal("a.wav#0", s.Tracks[0].Id);
            Assert.True(s.Playback["a.wav#0"].Mute);
        });
    }

    [Fact]
    public void Sidecar_MissingOrCorrupt_LoadsAsNull()
    {
        With(root =>
        {
            Assert.Null(TakeSidecar.Load(root));
            File.WriteAllText(Path.Combine(root, TakeSidecar.FileName), "{ not json");
            Assert.Null(TakeSidecar.Load(root));
        });
    }

    [Fact]
    public void TrackIds_AndPeakFileNames()
    {
        Assert.Equal("SmartMike+ 0D5E.wav#0", TrackInfo.MakeId("SmartMike+ 0D5E.wav", 0));
        Assert.Equal("Pixel 8/20261012_101010.mkv#2", TrackInfo.MakeId("Pixel 8\\20261012_101010.mkv", 2));
        Assert.Equal("Pixel 8_20261012_101010.mkv_2.peaks", TrackInfo.PeakFileName("Pixel 8/20261012_101010.mkv#2"));
        Assert.DoesNotContain(Path.GetInvalidFileNameChars().Where(c => c != '.'), c => TrackInfo.PeakFileName("a:b*c?d/e").Contains(c));
    }

    // ------------------------------------------------------------------ takes without sidecar

    [Fact]
    public void OldWavTake_GetsSidecarOnce()
    {
        With(root =>
        {
            var folder = Path.Combine(root, "20261012_101010");
            MakeWav(folder, "SmartMike+ 0D5E", 2);
            MakeWav(folder, "SmartMike+ 9F00", 2);
            var lib = new TakeLibrary(root);
            var t = lib.Load(folder)!;
            Assert.True(File.Exists(Path.Combine(folder, TakeSidecar.FileName)));
            Assert.Equal(2, t.Tracks.Count);
            Assert.Equal("SmartMike+ 0D5E.wav#0", t.Tracks[0].Id);
            Assert.Equal("SmartMike+ 0D5E", t.Tracks[0].Name);
            Assert.Equal("pcm_s16le", t.Tracks[0].Codec);
            Assert.Equal(44100, t.Tracks[0].SampleRate);
            Assert.InRange(t.Duration.TotalSeconds, 1.99, 2.01);
            Assert.Equal(RecordingFormat.Wav, t.Format);
            Assert.Equal(new DateTime(2026, 10, 12, 10, 10, 10), t.Time);
            Assert.Equal("2026-10-12 10:10", t.DisplayTitle);
            Assert.Equal(TakeStatus.Complete, t.Status);
            Assert.Equal(2 * (44 + 2 * 88200), t.TotalSize);
            Assert.False(t.IsRemote);
            Assert.Equal([TakeLibrary.ThisDevice], t.Devices);

            // second read uses the sidecar, not the files
            var stamp = File.GetLastWriteTimeUtc(Path.Combine(folder, TakeSidecar.FileName));
            Thread.Sleep(20);
            Assert.Equal(2, lib.Load(folder)!.Tracks.Count);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(folder, TakeSidecar.FileName)));
        });
    }

    [Fact]
    public void Probe_CanBePluggedIn_AndFallsBackWhenItFails()
    {
        With(root =>
        {
            var folder = Path.Combine(root, "20261012_101010");
            MakeWav(folder, "a", 1);
            MakeWav(folder, "b", 1);
            var lib = new TakeLibrary(root, file => Path.GetFileName(file) == "a.wav"
                ? new[] { new TrackInfo { Name = "from probe", Codec = "pcm_s16le", DurationMs = 5 } }
                : throw new InvalidDataException());
            var t = lib.Load(folder)!;
            Assert.Equal("from probe", t.Tracks[0].Name);
            Assert.Equal("a.wav#0", t.Tracks[0].Id);
            Assert.Equal("b", t.Tracks[1].Name); // fallback
            Assert.InRange(t.Tracks[1].DurationMs, 999, 1001);
        });
    }

    [Fact]
    public void FolderWithoutRecordings_IsNotATake()
    {
        With(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "20261012_101010"));
            File.WriteAllText(Path.Combine(root, "20261012_101010", "readme.txt"), "x");
            Directory.CreateDirectory(Path.Combine(root, "other"));
            var lib = new TakeLibrary(root);
            Assert.Empty(lib.Scan());
            Assert.False(File.Exists(Path.Combine(root, "20261012_101010", TakeSidecar.FileName)));
        });
    }

    // ------------------------------------------------------------------ scan, edit, trash

    [Fact]
    public void Scan_NewestFirst_SkipsTrashAndHiddenFolders()
    {
        With(root =>
        {
            MakeWav(Path.Combine(root, "20261010_080000"), "a");
            MakeWav(Path.Combine(root, "20261012_080000"), "a");
            MakeWav(Path.Combine(root, "20261011_080000"), "a");
            MakeWav(Path.Combine(root, ".trash", "20261013_080000"), "a");
            MakeWav(Path.Combine(root, ".share", "x"), "a");
            File.WriteAllText(Path.Combine(root, "20261012_080000", "plural.json.tmp"), "junk");
            var lib = new TakeLibrary(root);
            Assert.Equal(["20261012_080000", "20261011_080000", "20261010_080000"], lib.Scan().Select(t => t.Id));
            Assert.Empty(new TakeLibrary(Path.Combine(root, "missing")).Scan());
        });
    }

    [Fact]
    public void Scan_OneCorruptSidecarOrFile_DoesNotBreakTheRest()
    {
        With(root =>
        {
            var bad = Path.Combine(root, "20261012_080000");
            MakeWav(bad, "a");
            File.WriteAllText(Path.Combine(bad, TakeSidecar.FileName), "{{{");
            File.WriteAllBytes(Path.Combine(root, "20261011_080000.wav"), [1, 2, 3]);
            MakeWav(Path.Combine(root, "20261010_080000"), "a");
            File.WriteAllBytes(Path.Combine(Path.Combine(root, "20261010_080000"), "broken.mka"), [1, 2, 3]);
            var all = new TakeLibrary(root).Scan();
            Assert.Equal(2, all.Count);
            Assert.Single(all[0].Tracks); // corrupt sidecar rebuilt from the files
            Assert.Equal(2, all[1].Tracks.Count); // unreadable file kept as a stub track
        });
    }

    [Fact]
    public void RenameNotesPlayback_Persist_AndFolderStays()
    {
        With(root =>
        {
            var folder = Path.Combine(root, "20261012_101010");
            MakeWav(folder, "mic");
            var lib = new TakeLibrary(root);
            var t = lib.Rename(folder, "  Podcast / ep 4 ")!;
            Assert.Equal("Podcast / ep 4", t.Title);
            Assert.Equal("Podcast / ep 4", t.DisplayTitle);
            Assert.True(Directory.Exists(folder));
            lib.SetNotes(folder, "Clap at 0:03");
            lib.SetPlayback(folder, "mic.wav#0", gainDb: -6);
            lib.SetPlayback(folder, "mic.wav#0", mute: true);
            lib.SetPlayback(folder, "other#0", solo: true);
            t = lib.Load(folder)!;
            Assert.Equal("Clap at 0:03", t.Notes);
            Assert.Equal(-6, t.Playback["mic.wav#0"].GainDb);
            Assert.True(t.Playback["mic.wav#0"].Mute);
            Assert.False(t.Playback["mic.wav#0"].Solo);
            Assert.True(t.Playback["other#0"].Solo);
            lib.SetPlayback(folder, "other#0", solo: false);
            Assert.False(lib.Load(folder)!.Playback.ContainsKey("other#0"));
            Assert.Null(lib.Rename(folder, "")!.Title);
            Assert.Equal("2026-10-12 10:10", lib.Load(folder)!.DisplayTitle);
        });
    }

    [Fact]
    public void Delete_MovesToTrash_RemovesShareZip_AndRestoreBringsItBack()
    {
        With(root =>
        {
            var folder = Path.Combine(root, "20261012_101010");
            MakeWav(folder, "a");
            MakeWav(folder, "b");
            var lib = new TakeLibrary(root);
            lib.Rename(folder, "Keep me");
            var zip = TakeExport.GetShareFile(folder);
            var titled = TakeExport.GetShareFile(folder, "Keep me");
            Assert.True(File.Exists(zip) && File.Exists(titled));
            Assert.Equal("Keep me.zip", Path.GetFileName(titled));

            var trashed = lib.Delete(folder);
            Assert.Equal(Path.Combine(root, ".trash", "20261012_101010"), trashed);
            Assert.False(Directory.Exists(folder));
            Assert.False(File.Exists(zip));
            Assert.False(File.Exists(titled));
            Assert.Empty(lib.Scan());
            var list = lib.ListTrash();
            Assert.Single(list);
            Assert.NotNull(list[0].DeletedAt);
            Assert.Equal("Keep me", list[0].Title);

            var back = lib.Restore(trashed);
            Assert.Equal(folder, back);
            var t = lib.Load(back)!;
            Assert.Null(t.DeletedAt);
            Assert.Equal("Keep me", t.Title);
            Assert.Empty(lib.ListTrash());
            Assert.Single(lib.Scan());
        });
    }

    [Fact]
    public void Delete_SameNameTwice_GetsUniqueTrashFolders_AndDeleteNowRemoves()
    {
        With(root =>
        {
            var lib = new TakeLibrary(root);
            var folder = Path.Combine(root, "20261012_101010");
            MakeWav(folder, "a");
            var first = lib.Delete(folder);
            MakeWav(folder, "a");
            var second = lib.Delete(folder);
            Assert.NotEqual(first, second);
            Assert.Equal(2, lib.ListTrash().Count);
            lib.DeleteNow(first);
            Assert.False(Directory.Exists(first));
            Assert.Single(lib.ListTrash());
            Assert.Throws<ArgumentException>(() => lib.DeleteNow(root));
            Assert.Throws<ArgumentException>(() => lib.Delete(Path.Combine(root, ".trash")));
        });
    }

    [Fact]
    public void PurgeTrash_RemovesOnlyOldTakes_AndUsesDirectoryTimeWithoutDeletedAt()
    {
        With(root =>
        {
            var lib = new TakeLibrary(root);
            var oldFolder = Path.Combine(root, "20261001_000000");
            var newFolder = Path.Combine(root, "20261002_000000");
            var noStamp = Path.Combine(root, "20261003_000000");
            MakeWav(oldFolder, "a");
            MakeWav(newFolder, "a");
            MakeWav(noStamp, "a");
            var oldT = lib.Delete(oldFolder);
            var newT = lib.Delete(newFolder);
            var noT = lib.Delete(noStamp);
            TakeSidecar.Update(oldT, s => s.DeletedAt = DateTime.UtcNow.AddDays(-31));
            TakeSidecar.Update(newT, s => s.DeletedAt = DateTime.UtcNow.AddDays(-29));
            File.Delete(Path.Combine(noT, TakeSidecar.FileName));
            Directory.SetLastWriteTimeUtc(noT, DateTime.UtcNow.AddDays(-40));

            Assert.Equal(2, lib.PurgeTrash(TimeSpan.FromDays(30)));
            Assert.False(Directory.Exists(oldT));
            Assert.False(Directory.Exists(noT));
            Assert.True(Directory.Exists(newT));
            Assert.Equal(0, new TakeLibrary(Path.Combine(root, "nothing")).PurgeTrash(TimeSpan.FromDays(30)));
        });
    }

    [Fact]
    public void RecoverInterrupted_MarksRecordingTakes_ExceptTheCurrentOne()
    {
        With(root =>
        {
            var a = Path.Combine(root, "20261012_101010");
            var b = Path.Combine(root, "20261012_111111");
            var c = Path.Combine(root, "20261012_121212");
            foreach (var f in new[] { a, b, c }) { MakeWav(f, "mic", 2); }
            var lib = new TakeLibrary(root);
            lib.Load(a);
            lib.Load(b);
            lib.Load(c);
            TakeSidecar.Update(a, s => { s.Status = TakeStatus.Recording; s.Tracks[0].DurationMs = 0; });
            TakeSidecar.Update(b, s => s.Status = TakeStatus.Recording);
            using (var w = new PeakWriter(TrackInfo.PeakPath(a, "mic.wav#0"), 44100)) { w.Write(new short[44100]); }

            var recovered = lib.RecoverInterrupted(b + Path.DirectorySeparatorChar);
            Assert.Equal([a], recovered);
            var ta = lib.Load(a)!;
            Assert.Equal(TakeStatus.Recovered, ta.Status);
            Assert.InRange(ta.Tracks[0].DurationMs, 1999, 2001); // from the file (the peaks file only covers 1 s)
            Assert.Equal(TakeStatus.Recording, lib.Load(b)!.Status);
            Assert.Equal(TakeStatus.Complete, lib.Load(c)!.Status);
        });
    }

    // ------------------------------------------------------------------ remote takes

    private static void WriteManifest(string folder, string remoteStatus)
    {
        TakeManifest.Update(folder, m =>
        {
            m.TakeId = "t";
            m.Controller = "Surface";
            m.Devices.Clear();
            m.Device("c", "Surface").Role = "controller";
            m.Devices[0].StartOffsetMs = 100;
            var d = m.Device("p", "Pixel 8");
            d.Folder = "Pixel 8";
            d.StartOffsetMs = 340.5;
            d.Status = remoteStatus;
        });
    }

    [Fact]
    public void RemoteTake_ShiftsSubFolderTracksByTheOffsetRelativeToTheController()
    {
        With(root =>
        {
            var folder = Path.Combine(root, "20261012_101010");
            MakeWav(folder, "Surface mic", 3);
            MakeWav(Path.Combine(folder, "Pixel 8"), "Phone mic", 2);
            WriteManifest(folder, "recording");
            var lib = new TakeLibrary(root);

            var t = lib.Load(folder)!;
            Assert.Equal(TakeStatus.Syncing, t.Status);
            Assert.True(t.IsRemote);
            Assert.Equal(["Surface", "Pixel 8"], t.Devices);
            var phone = t.Tracks.Single(x => x.IsRemote);
            Assert.Equal("Pixel 8", phone.Device);
            Assert.Equal("Pixel 8/Phone mic.wav#0", phone.Id);
            Assert.Equal(240.5, phone.StartOffsetMs);
            Assert.Equal(0, t.Tracks.Single(x => !x.IsRemote).StartOffsetMs);
            Assert.InRange(t.Duration.TotalSeconds, 2.99, 3.01);
            // not stored while the device is still sending
            Assert.DoesNotContain(TakeSidecar.Load(folder)!.Tracks, x => x.IsRemote);

            WriteManifest(folder, "synced");
            t = lib.Load(folder)!;
            Assert.Equal(TakeStatus.Complete, t.Status);
            Assert.Equal(240.5, TakeSidecar.Load(folder)!.Tracks.Single(x => x.IsRemote).StartOffsetMs);

            // a corrected offset in take.json moves the stored track
            TakeManifest.Update(folder, m => m.Devices[1].StartOffsetMs = 100);
            Assert.Equal(0, lib.Load(folder)!.Tracks.Single(x => x.IsRemote).StartOffsetMs);
            // device that started earlier: negative shift
            TakeManifest.Update(folder, m => m.Devices[1].StartOffsetMs = 60);
            Assert.Equal(-40, lib.Load(folder)!.Tracks.Single(x => x.IsRemote).StartOffsetMs);
        });
    }

    // ------------------------------------------------------------------ share file

    [Fact]
    public void ShareFile_OfSingleWavTake_IsStillTheWav_AndIgnoresLibraryFiles()
    {
        With(root =>
        {
            var folder = Path.Combine(root, "20261012_101010");
            var wav = MakeWav(folder, "mic");
            var lib = new TakeLibrary(root);
            lib.Rename(folder, "Some title");
            File.WriteAllBytes(Path.Combine(folder, TakeSidecar.PosterFileName), [1]);
            using (var w = new PeakWriter(TrackInfo.PeakPath(folder, "mic.wav#0"), 44100)) { w.Write(new short[1000]); }
            Assert.Equal(wav, TakeExport.GetShareFile(folder));
            Assert.Equal(wav, TakeExport.GetShareFile(folder, "Some title"));
        });
    }

    [Fact]
    public void ShareZip_IsNamedAfterTheTitle_AndKeepsTakeJson()
    {
        With(root =>
        {
            var folder = Path.Combine(root, "20261012_101010");
            MakeWav(folder, "mic");
            MakeWav(Path.Combine(folder, "Pixel 8"), "phone");
            WriteManifest(folder, "synced");
            new TakeLibrary(root).Rename(folder, "Ep 4: \"the\" end?");
            var zip = TakeExport.GetShareFile(folder, "Ep 4: \"the\" end?");
            Assert.Equal(TakeExport.SanitizeTitle("Ep 4: \"the\" end?") + ".zip", Path.GetFileName(zip));
            if (OperatingSystem.IsWindows()) { Assert.Equal("Ep 4_ _the_ end_.zip", Path.GetFileName(zip)); }
            using var za = ZipFile.OpenRead(zip);
            Assert.Equal(["Pixel 8/phone.wav", "mic.wav", "take.json"], za.Entries.Select(e => e.FullName).OrderBy(x => x, StringComparer.Ordinal));
            // untitled stays next to the folder
            Assert.Equal(folder + ".zip", TakeExport.GetShareFile(folder));
        });
    }
}

/// <summary>Real <see cref="TakeRecorder"/> takes through the hub with fake mics: sidecar, peaks, poster, and re-reading without the sidecar.</summary>
[Collection("timing")]
public class LibraryRecordingTests
{
    private sealed class FakeBt : IBluetoothPlatform
    {
        public IReadOnlyList<PairedDevice> GetPairedDevices() => [];
        public Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeAudio : IAudioPlatform
    {
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate) => throw new NotSupportedException();
    }

    private sealed class FakeAac(int priming = 1024) : IAacEncoderFactory
    {
        public IAacEncoder Create(int sampleRate, int channels, int bitRate) => new Enc(priming);

        private sealed class Enc(int priming) : IAacEncoder
        {
            private int _pending;
            public int PrimingSamples => priming;
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
        public event Action<PreviewFrame>? PreviewFrameReady;
        public event Action<EncodedSample>? SampleEncoded;
        public bool Encoding;
        public bool NoKeyFrame; // the camera never sends an IDR
        public int PreviewSubscribers => PreviewFrameReady?.GetInvocationList().Length ?? 0;

        public void RaisePreview(PreviewFrame f) => PreviewFrameReady?.Invoke(f);

        public void StartEncoding()
        {
            Encoding = true;
            Task.Run(async () =>
            {
                byte[] key = [0, 0, 0, 1, 0x67, 0x42, 0xC0, 0x1F, 0xDA, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80, 0, 0, 1, 0x65, 1, 2, 3];
                byte[] delta = [0, 0, 1, 0x41, 9, 9, 9];
                for (var i = 0; i < 20 && Encoding; i++)
                {
                    SampleEncoded?.Invoke(new EncodedSample(i == 0 && !NoKeyFrame ? key : delta, MediaClock.Now, i == 0 && !NoKeyFrame));
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

    private static (MikeHub Hub, List<MikeSession> Sessions) NewHub(int mics, int priming = 1024)
    {
        var hub = new MikeHub(new FakeBt(), new FakeAudio(), new FakeAac(priming), new FakeVideo());
        var sessions = new List<MikeSession>();
        for (var i = 0; i < mics; i++)
        {
            var ch = hub.Add(new PairedDevice(new BluetoothAddress(0x2836380D5E00UL + (ulong)i), "SmartMike+"));
            typeof(MikeSession).GetProperty("State")!.SetValue(ch.Session!, MikeState.Streaming);
            sessions.Add(ch.Session!);
        }
        return (hub, sessions);
    }

    private static void Raise(MikeSession s, short[] pcm)
    {
        var f = (PcmHandler?)typeof(MikeSession).GetField("PcmReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(s);
        f?.Invoke(s, pcm, 44100);
    }

    private static async Task Feed(List<MikeSession> sessions, double seconds, Action? tick = null)
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
            tick?.Invoke();
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task WavTake_WritesSidecarRecordingThenComplete_AndPeaksMatchTheWav()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_libtake_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(2);
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Wav };
            var folder = hub.StartRecordingAll(root);

            var during = TakeSidecar.Load(folder)!;
            Assert.Equal(TakeStatus.Recording, during.Status);
            Assert.Equal(2, during.Tracks.Count);
            Assert.All(during.Tracks, t => Assert.Equal("pcm_s16le", t.Codec));
            Assert.Null(during.Title);

            await Feed(sessions, 1.5);
            var take = await hub.StopRecordingAllAsync();
            Assert.Empty(take.Errors);
            Assert.Equal(Path.Combine(folder, TakeSidecar.FileName), take.SidecarPath);
            Assert.Equal(2, take.Tracks.Count);

            var s = TakeSidecar.Load(folder)!;
            Assert.Equal(TakeStatus.Complete, s.Status);
            Assert.Equal(take.Tracks.Select(t => t.Id), s.Tracks.Select(t => t.Id));
            for (var i = 0; i < 2; i++)
            {
                var t = s.Tracks[i];
                Assert.Equal(TrackInfo.MakeId(t.File, 0), t.Id);
                Assert.False(t.File.Contains('\\'));
                Assert.Equal(hub.Channels[i].Source.DisplayName, t.Name);
                Assert.InRange(t.DurationMs, 1000, 2600);

                var wav = File.ReadAllBytes(Path.Combine(folder, t.File));
                var pcm = new short[(wav.Length - 44) / 2];
                Buffer.BlockCopy(wav, 44, pcm, 0, pcm.Length * 2);
                Assert.Equal(pcm.Length * 1000.0 / 44100, t.DurationMs, 1);

                var peaks = PeakReader.Open(TrackInfo.PeakPath(folder, t.Id))!;
                Assert.Equal(pcm.Length, peaks.SampleCount);
                var (l0, l1) = PeakBuilder.Build(pcm);
                var min = new short[l0.Length / 2];
                var max = new short[l0.Length / 2];
                peaks.GetRange(0, 256, min, max);
                for (var k = 0; k < min.Length; k++)
                {
                    Assert.Equal(l0[2 * k], min[k]);
                    Assert.Equal(l0[2 * k + 1], max[k]);
                }
                var m1 = new short[l1.Length / 2];
                var x1 = new short[l1.Length / 2];
                peaks.GetRange(0, 16384, m1, x1);
                for (var k = 0; k < m1.Length; k++)
                {
                    Assert.Equal(l1[2 * k], m1[k]);
                    Assert.Equal(l1[2 * k + 1], x1[k]);
                }
            }

            // library sees the take, with no probing needed, and sharing 2 WAVs makes a zip without the library files
            var info = new TakeLibrary(root).Scan().Single();
            Assert.Equal(2, info.Tracks.Count);
            Assert.Equal(TakeStatus.Complete, info.Status);
            using var za = ZipFile.OpenRead(TakeExport.GetShareFile(folder, "Test"));
            Assert.Equal(2, za.Entries.Count);
            Assert.DoesNotContain(za.Entries, e => e.FullName.Contains("plural") || e.FullName.Contains(".peaks"));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Theory]
    [InlineData(RecordingFormat.Wav, 2, 2)]
    [InlineData(RecordingFormat.Mp3, 2, 2)]
    [InlineData(RecordingFormat.M4a, 2, 2)]
    [InlineData(RecordingFormat.Mka, 2, 1)]
    [InlineData(RecordingFormat.Mkv, 2, 1)]
    [InlineData(RecordingFormat.Mp4, 2, 1)]
    public async Task EveryFormat_SidecarMatchesWhatTheFallbackProbeFindsInTheFiles(RecordingFormat format, int mics, int files)
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_libfmt_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(mics);
            hub.Camera = new FakeCamera();
            hub.RecordingSettings = new RecordingSettings { Format = format };
            var folder = hub.StartRecordingAll(root);
            await Feed(sessions, 1.2);
            var take = await hub.StopRecordingAllAsync();
            Assert.Empty(take.Errors);
            Assert.Equal(files, take.Files.Count);

            var written = TakeSidecar.Load(folder)!;
            var video = format.IsVideo();
            Assert.Equal(mics + (video ? 1 : 0), written.Tracks.Count);
            Assert.Equal(video, written.Tracks.Any(t => t.IsVideo));
            Assert.DoesNotContain(written.Tracks, t => t.File.Contains('/') || t.File.Contains('\\'));

            // ids are unique, indices follow the container's track order
            Assert.Equal(written.Tracks.Count, written.Tracks.Select(t => t.Id).Distinct().Count());
            if (format.IsMultiTrack())
            {
                Assert.Equal(Enumerable.Range(0, written.Tracks.Count), written.Tracks.Select(t => t.TrackIndex));
            }
            if (video)
            {
                var v = written.Tracks.Single(t => t.IsVideo);
                Assert.Equal((640, 480, 90, "h264"), (v.Width, v.Height, v.Rotation, v.Codec));
                Assert.Equal("Video", v.Name);
            }

            // same take read back from the files alone
            File.Delete(Path.Combine(folder, TakeSidecar.FileName));
            var probed = new TakeLibrary(root).Load(folder)!;
            Assert.Equal(format, probed.Format);
            Assert.Equal(written.Tracks.Count, probed.Tracks.Count);
            Assert.Equal(video, probed.HasVideo);
            foreach (var w in written.Tracks)
            {
                var p = probed.Tracks.Single(t => t.Id == w.Id);
                Assert.Equal(w.Kind, p.Kind);
                Assert.Equal(w.Codec, p.Codec);
                Assert.Equal(w.File, p.File);
                Assert.Equal(w.TrackIndex, p.TrackIndex);
                if (format.IsMultiTrack()) { Assert.Equal(w.Name, p.Name); }
            }
            if (video)
            {
                var pv = probed.Tracks.Single(t => t.IsVideo);
                Assert.Equal((640, 480, 90), (pv.Width, pv.Height, pv.Rotation));
            }
            if (format is RecordingFormat.Wav or RecordingFormat.Mka or RecordingFormat.Mkv)
            {
                Assert.InRange(probed.Duration.TotalSeconds, 1.0, 2.6);
            }
            Assert.True(File.Exists(Path.Combine(folder, TakeSidecar.FileName)));

            // share: a single media file stays a plain file (sidecar and peaks do not count)
            var share = TakeExport.GetShareFile(folder);
            Assert.Equal(files == 1, share.EndsWith(format.Extension()));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Theory]
    [InlineData(RecordingFormat.M4a, 2112)]
    [InlineData(RecordingFormat.M4a, 1024)]
    [InlineData(RecordingFormat.Mp4, 2112)]
    public async Task AacTakes_StoreTheEncodersPrimingInTheEditList(RecordingFormat format, int priming)
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_libprime_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(2, priming);
            hub.Camera = new FakeCamera();
            hub.RecordingSettings = new RecordingSettings { Format = format };
            hub.StartRecordingAll(root);
            await Feed(sessions, 1.2);
            var take = await hub.StopRecordingAllAsync();
            Assert.Empty(take.Errors);
            foreach (var file in take.Files)
            {
                using var r = MediaReader.Open(file);
                foreach (var t in r.Tracks.Where(t => t.Kind == MediaKind.Audio))
                {
                    r.SelectTracks([t.Index]);
                    Assert.True(r.TryRead(out var first)); // sequential: the first packet carries the priming
                    Assert.Equal(-priming * 10_000_000L / 44100, first.Timestamp);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task VideoTake_SavesAnUprightPoster_AboutASecondIn()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_libposter_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(1);
            var camera = new FakeCamera();
            hub.Camera = camera;
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Mkv };
            var folder = hub.StartRecordingAll(root);
            Assert.Equal(1, camera.PreviewSubscribers);

            // 4x2 frame, each pixel's blue channel = its index
            var bgra = new byte[4 * 2 * 4];
            for (var i = 0; i < 8; i++) { bgra[4 * i] = (byte)(10 + i); bgra[4 * i + 1] = 1; bgra[4 * i + 2] = 2; bgra[4 * i + 3] = 255; }
            var early = new byte[bgra.Length]; // an all-black frame from the first second must not become the poster
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Feed(sessions, 1.6, () =>
            {
                camera.RaisePreview(new PreviewFrame(sw.Elapsed.TotalSeconds < 0.9 ? early : bgra, 4, 2, 16));
            });
            var take = await hub.StopRecordingAllAsync();
            Assert.Empty(take.Errors);
            Assert.Equal(0, camera.PreviewSubscribers);

            var poster = Path.Combine(folder, TakeSidecar.PosterFileName);
            Assert.True(File.Exists(poster));
            var (w, h, rgb) = PosterTests.DecodePng(File.ReadAllBytes(poster));
            Assert.Equal((2, 4), (w, h)); // rotated 90 degrees clockwise
            // top row = left column of the source, bottom to top: pixels 4 then 0
            Assert.Equal(new byte[] { 2, 1, 14 }, rgb.AsSpan(0, 3).ToArray());
            Assert.Equal(new byte[] { 2, 1, 10 }, rgb.AsSpan(3, 3).ToArray());
            Assert.Equal(poster, new TakeLibrary(root).Scan().Single().PosterPath);
            Assert.False(File.Exists(poster + ".tmp"));
            // the poster is not part of what gets shared
            Assert.EndsWith(".mkv", TakeExport.GetShareFile(folder));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task ShortVideoTake_HasNoPoster_AndStillUnsubscribes()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_libposter2_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(1);
            var camera = new FakeCamera();
            hub.Camera = camera;
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Mp4 };
            var folder = hub.StartRecordingAll(root);
            await Feed(sessions, 0.3, () => camera.RaisePreview(new PreviewFrame(new byte[32], 4, 2, 16)));
            await hub.StopRecordingAllAsync();
            Assert.Equal(0, camera.PreviewSubscribers);
            Assert.False(File.Exists(Path.Combine(folder, TakeSidecar.PosterFileName)));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task BrokenPosterFrame_NeverHangsOrFailsStop_AndIsReported()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_libposter3_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(1);
            var camera = new FakeCamera();
            hub.Camera = camera;
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Mkv };
            var folder = hub.StartRecordingAll(root);
            // a negative width makes the poster encoder throw an OverflowException, which used to escape the poster task
            await Feed(sessions, 1.4, () => camera.RaisePreview(new PreviewFrame(new byte[32], -4, 2, 16)));
            var stop = hub.StopRecordingAllAsync();
            Assert.Same(stop, await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10))));
            var take = await stop;
            Assert.Contains(take.Errors, e => e.StartsWith("poster"));
            Assert.False(File.Exists(Path.Combine(folder, TakeSidecar.PosterFileName)));
            Assert.Equal(TakeStatus.Complete, TakeSidecar.Load(folder)!.Status);
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task Mp4Take_WithoutKeyFrame_ReportsNoVideo_AndKeepsTheAudio()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_libnokey_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(1);
            hub.Camera = new FakeCamera { NoKeyFrame = true };
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Mp4 };
            var folder = hub.StartRecordingAll(root);
            await Feed(sessions, 0.6);
            var take = await hub.StopRecordingAllAsync();
            Assert.Contains(take.Errors, e => e.Contains("No video was recorded"));
            var file = Assert.Single(take.Files);
            using (var r = new Mp4Reader(File.OpenRead(file)))
            {
                Assert.Equal(MediaKind.Audio, Assert.Single(r.Tracks).Kind);
            }
            var tracks = TakeSidecar.Load(folder)!.Tracks;
            var audio = Assert.Single(tracks); // the sidecar no longer lists a video track the file does not have
            Assert.Equal((TrackKinds.Audio, 0), (audio.Kind, audio.TrackIndex));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }
}

public class LibraryRobustnessTests
{
    private static void With(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_libr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private static string Take(string root, string name, string status = TakeStatus.Complete)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        using (var w = new WavWriter(Path.Combine(folder, "a.wav"), 44100, 1)) { w.Write(new short[4410]); }
        TakeSidecar.Update(folder, s =>
        {
            s.Status = status;
            s.Title = "keep me";
            s.Notes = "notes";
            s.Tracks = [new TrackInfo { Id = "a.wav#0", File = "a.wav", DurationMs = 100, Codec = "pcm_s16le" }];
        });
        return folder;
    }

    [Fact]
    public void Sidecar_ThatCannotBeRead_IsNotReplaced()
    {
        With(root =>
        {
            var folder = Take(root, "20261012_101010");
            var path = Path.Combine(folder, TakeSidecar.FileName);
            var before = File.ReadAllText(path);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // an AV scanner holding the file
            {
                Assert.ThrowsAny<IOException>(() => TakeSidecar.Update(folder, s => s.Notes = "x"));
                Assert.ThrowsAny<IOException>(() => TakeSidecar.Load(folder));
            }
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal("keep me", TakeSidecar.Load(folder)!.Title);
        });
    }

    [Fact]
    public void Scan_SkipsATakeWhoseSidecarIsLocked_InsteadOfRebuildingIt()
    {
        With(root =>
        {
            var folder = Take(root, "20261012_101010");
            var path = Path.Combine(folder, TakeSidecar.FileName);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Empty(new TakeLibrary(root).Scan());
            }
            Assert.Equal("keep me", new TakeLibrary(root).Scan().Single().Title);
        });
    }

    [Fact]
    public void CorruptSidecar_IsKeptAsBak_BeforeItIsRewritten()
    {
        With(root =>
        {
            var folder = Take(root, "20261012_101010");
            var path = Path.Combine(folder, TakeSidecar.FileName);
            File.WriteAllText(path, "{ not json");
            TakeSidecar.Update(folder, s => s.Notes = "fresh");
            Assert.Equal("{ not json", File.ReadAllText(path + ".bak"));
            Assert.Equal("fresh", TakeSidecar.Load(folder)!.Notes);
            Assert.False(File.Exists(path + ".tmp"));
        });
    }

    [Fact]
    public void Update_DoesNotCreateAFolderThatIsGone_UnlessAsked()
    {
        With(root =>
        {
            var gone = Path.Combine(root, "gone");
            Assert.Throws<DirectoryNotFoundException>(() => TakeSidecar.Update(gone, s => s.Notes = "x"));
            Assert.Throws<DirectoryNotFoundException>(() => TakeSidecar.Save(gone, new TakeSidecar()));
            Assert.False(Directory.Exists(gone));
            TakeSidecar.Update(gone, s => s.Notes = "x", createFolder: true);
            Assert.True(File.Exists(Path.Combine(gone, TakeSidecar.FileName)));
        });
    }

    [Fact]
    public void Delete_RefusesATakeBeingRecorded_ButAcceptsARecoveredOne()
    {
        With(root =>
        {
            var lib = new TakeLibrary(root);
            var live = Take(root, "20261012_101010", TakeStatus.Recording);
            Assert.Throws<InvalidOperationException>(() => lib.Delete(live));
            Assert.True(Directory.Exists(live));

            var done = Take(root, "20261012_101011");
            Assert.Throws<InvalidOperationException>(() => lib.Delete(done, activeFolder: done));
            Assert.True(Directory.Exists(done));

            var recovered = Take(root, "20261012_101012", TakeStatus.Recovered);
            var trashed = lib.Delete(recovered);
            Assert.False(Directory.Exists(recovered));
            Assert.NotNull(TakeSidecar.Load(trashed)!.DeletedAt);
        });
    }

    [Fact]
    public void Delete_MovesFirst_SoALateWriteCannotResurrectTheFolder()
    {
        With(root =>
        {
            var lib = new TakeLibrary(root);
            var folder = Take(root, "20261012_101010");
            var trashed = lib.Delete(folder);
            Assert.False(Directory.Exists(folder));
            // e.g. the recorder completing, or a rename from the UI, after the take went to the trash
            Assert.ThrowsAny<IOException>(() => TakeSidecar.Update(folder, s => s.Status = TakeStatus.Complete));
            Assert.False(Directory.Exists(folder));

            var restored = lib.Restore(trashed);
            Assert.Null(TakeSidecar.Load(restored)!.DeletedAt);
            Assert.Equal("keep me", TakeSidecar.Load(restored)!.Title);
            Assert.False(Directory.Exists(trashed));
        });
    }

    private static byte[] HostileMp4() =>
        TinyMp4.File(TinyMp4.Box("trak", TinyMp4.Box("tkhd"))); // the last box ends exactly at the end of moov: reads past it

    [Fact]
    public void DamagedMp4_DoesNotMakeScanThrow()
    {
        With(root =>
        {
            Take(root, "20261012_101010");
            var bad = Path.Combine(root, "20261012_101011");
            Directory.CreateDirectory(bad);
            File.WriteAllBytes(Path.Combine(bad, "x.mp4"), HostileMp4());
            Assert.Empty(TakeProbe.Basic(Path.Combine(bad, "x.mp4")));
            var takes = new TakeLibrary(root, probe: _ => []).Scan();
            Assert.Equal(2, takes.Count);
        });
    }

    [Fact]
    public void Basic_IgnoresNonAudioVideoTracks_LikeMp4Reader()
    {
        With(root =>
        {
            var file = Path.Combine(root, "t.mp4");
            File.WriteAllBytes(file, TinyMp4.File(
                TinyMp4.Trak("text", 1000, 5000),
                TinyMp4.Trak("soun", 44100, 44100, TinyMp4.Box("stsd", TinyMp4.U32(0), TinyMp4.U32(0))),
                TinyMp4.Trak("tmcd", 30, 30)));
            var tracks = TakeProbe.Basic(file);
            var t = Assert.Single(tracks);
            Assert.Equal(TrackKinds.Audio, t.Kind);
            Assert.Equal(1000, t.DurationMs, 0.5);
            using var r = new Mp4Reader(File.OpenRead(file));
            Assert.Equal(tracks.Count, r.Tracks.Count);
        });
    }
}
