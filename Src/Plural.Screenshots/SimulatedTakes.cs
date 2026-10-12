using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Plural.Core.Library;
using Plural.Core.Remote;

namespace Plural.Screenshots;

/// <summary>Fake take folders (sparse media files, sidecar, peaks, posters) for the Takes page screenshots.</summary>
internal static class SimulatedTakes
{
    private sealed record Spec(string Folder, string? Title, string? Notes, int Minutes, string Ext, string[] Mics, bool Video = false,
        string? RemoteDevice = null, string Status = TakeStatus.Complete, string[]? RemoteMics = null, int DaysAgo = 0, double Loud = 1);

    public static void Create(string root)
    {
        var now = DateTime.Now;
        Directory.CreateDirectory(root);
        var specs = new[]
        {
            new Spec(Stamp(now.AddMinutes(-35)), "Interview with Maria", "Cafe, loud espresso machine. Maria's mic is the SmartMike with the red clip.", 18,
                ".wav", ["Maria", "Peter", "Room"], Loud: 1.0),
            new Spec(Stamp(now.AddHours(-3)), null, null, 4, ".mkv", ["Camera mic", "SmartMike+ 91C2"], Video: true, Loud: 0.6),
            new Spec(Stamp(now.AddDays(-1).AddHours(-2)), "Band rehearsal", "Song 3 is the good one.", 42, ".mka",
                ["Vocals", "Guitar", "Bass", "Drums"], RemoteDevice: "Galaxy S24", RemoteMics: ["Drums overhead"], Loud: 1.4),
            new Spec(Stamp(now.AddDays(-2)), null, null, 7, ".m4a", ["Built-in microphone"], Status: TakeStatus.Recovered, Loud: 0.3),
            new Spec(Stamp(now.AddDays(-4)), "Podcast ep. 12", null, 61, ".mp3", ["Host", "Guest"], Loud: 0.8),
            new Spec(Stamp(now.AddDays(-9)), "Street ambience", "Rain, tram line 4.", 2, ".mp4", ["Phone mic"], Video: true, Loud: 0.2),
            new Spec(Stamp(now.AddDays(-12)), null, null, 12, ".wav", ["USB microphone"], RemoteDevice: "Studio laptop", Status: "syncing",
                RemoteMics: ["Laptop mic"], Loud: 0.9),
        };
        var i = 0;
        foreach (var s in specs)
        {
            Make(root, s, ++i);
        }
    }

    private static string Stamp(DateTime t) => t.ToString("yyyyMMdd_HHmmss");

    private static void Make(string root, Spec s, int seed)
    {
        var folder = Path.Combine(root, s.Folder);
        Directory.CreateDirectory(folder);
        var sc = new TakeSidecar
        {
            Created = DateTime.ParseExact(s.Folder[..15], "yyyyMMdd_HHmmss", null),
            Title = s.Title,
            Notes = s.Notes,
            Status = s.Status == "syncing" ? TakeStatus.Complete : s.Status,
        };
        var rng = new Random(seed * 7919);
        var multi = s.Ext is ".mka" or ".mkv" or ".mp4" or ".m4a" && s.Mics.Length > 1;
        long size = 0;
        var milli = s.Minutes * 60_000.0;

        void Track(string kind, string file, int index, string name, string? device, double offset, double loud)
        {
            var t = new TrackInfo
            {
                Kind = kind,
                File = file,
                TrackIndex = index,
                Name = name,
                Device = device,
                StartOffsetMs = offset,
                DurationMs = milli,
                Codec = kind == TrackKinds.Video ? "h264" : s.Ext == ".wav" ? "pcm_s16le" : s.Ext == ".mp3" ? "mp3" : s.Ext == ".mka" ? "flac" : "aac",
                SampleRate = 44100,
                Channels = 1,
                Width = kind == TrackKinds.Video ? 1920 : 0,
                Height = kind == TrackKinds.Video ? 1080 : 0,
            };
            t.Id = TrackInfo.MakeId(file, index);
            sc.Tracks.Add(t);
            if (kind == TrackKinds.Audio)
            {
                WritePeaks(TrackInfo.PeakPath(folder, t.Id), s.Minutes, loud, rng);
            }
        }

        string Sparse(string rel, long bytes)
        {
            var path = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(path)) { fs.SetLength(bytes); }
            size += bytes;
            return rel;
        }

        var bytesPerMic = s.Ext switch { ".wav" => 88_200L, ".mka" => 40_000L, ".mp3" => 32_000L, _ => 24_000L };
        if (multi)
        {
            var file = Sparse("take" + s.Ext, (long)(bytesPerMic * s.Minutes * 60 * s.Mics.Length) + (s.Video ? s.Minutes * 60 * 1_500_000L : 0));
            var idx = 0;
            if (s.Video) { Track(TrackKinds.Video, file, idx++, "Video", null, 0, 0); }
            for (var m = 0; m < s.Mics.Length; m++) { Track(TrackKinds.Audio, file, idx++, s.Mics[m], null, 0, s.Loud * (0.5 + rng.NextDouble())); }
        }
        else
        {
            for (var m = 0; m < s.Mics.Length; m++)
            {
                var file = Sparse($"{s.Mics[m].Replace(' ', '_')}{s.Ext}", bytesPerMic * s.Minutes * 60);
                Track(s.Video ? TrackKinds.Video : TrackKinds.Audio, file, 0, s.Mics[m], null, 0, s.Loud * (0.5 + rng.NextDouble()));
            }
        }
        if (s.RemoteDevice is { } dev)
        {
            foreach (var rm in s.RemoteMics ?? [])
            {
                var file = Sparse($"{dev}/{rm.Replace(' ', '_')}.wav", 88_200L * s.Minutes * 60);
                Track(TrackKinds.Audio, file, 0, rm, dev, 420, s.Loud);
            }
            if (s.Status == "syncing")
            {
                TakeManifest.Update(folder, m =>
                {
                    m.Controller = "Studio PC";
                    var d = m.Device("remote-1", dev);
                    d.Role = "remote";
                    d.Status = "syncing";
                });
            }
        }
        if (s.Video) { WritePoster(Path.Combine(folder, TakeSidecar.PosterFileName), seed); }
        TakeSidecar.Save(folder, sc);
        _ = size;
    }

    private static void WritePeaks(string path, int minutes, double loud, Random rng)
    {
        using var w = new PeakWriter(path, 44100);
        var buf = new short[44100];
        var phase = rng.NextDouble() * 10;
        var seconds = Math.Min(minutes * 60, 20 * 60);
        for (var sec = 0; sec < seconds; sec++)
        {
            // speech-like bursts: a slow envelope with pauses, plus noise
            var env = Math.Max(0, Math.Sin((sec + phase) * 0.45) * Math.Sin((sec + phase) * 0.11 + 1) + 0.1);
            var amp = Math.Clamp(env * 14000 * loud, 200, 30000);
            for (var i = 0; i < buf.Length; i++)
            {
                var wobble = 0.6 + 0.4 * Math.Sin(i * 0.002 + sec);
                buf[i] = (short)((rng.NextDouble() * 2 - 1) * amp * wobble);
            }
            w.Write(buf);
        }
        w.Complete();
    }

    private static void WritePoster(string path, int seed)
    {
        var bmp = new WriteableBitmap(new PixelSize(320, 180), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            var data = new int[320 * 180];
            for (var y = 0; y < 180; y++)
            {
                for (var x = 0; x < 320; x++)
                {
                    var r = (byte)(40 + 120 * x / 320 + seed * 9 % 60);
                    var g = (byte)(60 + 100 * y / 180);
                    var b = (byte)(130 + 60 * (x + y) / 500);
                    data[y * 320 + x] = (0xFF << 24) | (r << 16) | (g << 8) | b;
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(data, 0, fb.Address, data.Length);
        }
        bmp.Save(path);
    }
}
