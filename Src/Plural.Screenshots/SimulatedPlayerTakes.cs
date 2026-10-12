using Plural.Core.Library;

namespace Plural.Screenshots;

/// <summary>
/// Takes for the Player page screenshots: valid (silent, sparse) WAV files so the audio tracks open, peaks files with speech-like
/// bursts for the waveforms, and video tracks (sparse, unreadable on purpose) whose frames the screenshot tool draws itself.
/// </summary>
internal static class SimulatedPlayerTakes
{
    private sealed record Audio(string Name, string? Device = null, double OffsetMs = 0, double Loud = 1);

    private sealed record Video(string Name, bool Portrait, string? Device = null, double OffsetMs = 0);

    private sealed record Spec(string Title, DateTime Time, int Seconds, Audio[] Audios, Video[] Videos, double[]? PausesSec = null,
        (int Track, double From, double To)[]? Dropouts = null, string? Notes = null);

    public static string[] Titles { get; } =
    [
        "Cafe interview", "Two phones", "Panel talk", "Three portraits", "Studio session", "Field recording",
    ];

    public static void Create(string root)
    {
        var now = DateTime.Now;
        var specs = new[]
        {
            // 1 landscape video, 3 mics
            new Spec("Cafe interview", now.AddMinutes(-80), 214,
                [new("Maria"), new("Peter", Loud: 0.8), new("Room", Loud: 0.35)],
                [new("Camera", false)], Notes: "Cafe on the corner, espresso machine in the background."),
            // landscape + portrait
            new Spec("Two phones", now.AddHours(-5), 156,
                [new("Alice"), new("Bob", "Pixel 9", 420, 0.9)],
                [new("Studio cam", false), new("Pixel 9", true, "Pixel 9", 420)]),
            // 1 landscape + 2 portrait, 2 remote devices with offsets
            new Spec("Panel talk", now.AddDays(-1).AddHours(-3), 268,
                [new("Host"), new("Guest 1", "Galaxy S24", 640, 1.2), new("Guest 2", "Pixel 9", 1180, 0.85), new("Room", Loud: 0.3)],
                [new("Stage cam", false), new("Galaxy S24", true, "Galaxy S24", 640), new("Pixel 9", true, "Pixel 9", 1180)],
                PausesSec: [96.0]),
            // 3 portrait phones
            new Spec("Three portraits", now.AddDays(-2), 128,
                [new("Mic A"), new("Mic B", "Galaxy S24", 300), new("Mic C", "Pixel 9", 760)],
                [new("Phone 1", true), new("Phone 2", true, "Galaxy S24", 300), new("Phone 3", true, "Pixel 9", 760)]),
            // audio only: 6 tracks, a dropout and pauses
            new Spec("Studio session", now.AddDays(-3), 342,
                [new("Vocals"), new("Guitar", Loud: 1.3), new("Bass", Loud: 1.1), new("Drums L", Loud: 1.5), new("Drums R", Loud: 1.4),
                 new("Room", "Galaxy S24", 520, 0.5)],
                [], PausesSec: [88.5, 171.2, 240.0], Dropouts: [(1, 120.0, 131.5), (2, 120.0, 131.5)]),
            // 2 audio tracks
            new Spec("Field recording", now.AddDays(-6), 95,
                [new("Left mic", Loud: 0.6), new("Right mic", Loud: 0.55)], []),
        };
        var seed = 100;
        foreach (var s in specs) { Make(root, s, ++seed); }
    }

    private static void Make(string root, Spec s, int seed)
    {
        var folder = Path.Combine(root, s.Time.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(folder);
        var rng = new Random(seed * 31);
        var sc = new TakeSidecar { Created = s.Time, Title = s.Title, Notes = s.Notes, Status = TakeStatus.Complete };
        var ms = s.Seconds * 1000.0;

        foreach (var v in s.Videos)
        {
            var rel = (v.Device is null ? "" : v.Device + "/") + v.Name.Replace(' ', '_') + ".mp4";
            var path = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(path)) { fs.SetLength(2_000_000); }
            var t = new TrackInfo
            {
                Kind = TrackKinds.Video, File = rel, TrackIndex = 0, Name = v.Name, Device = v.Device, StartOffsetMs = v.OffsetMs,
                DurationMs = ms, Codec = "h264", Width = 1920, Height = 1080, Rotation = v.Portrait ? 90 : 0,
            };
            t.Id = TrackInfo.MakeId(rel, 0);
            sc.Tracks.Add(t);
        }
        var index = 0;
        foreach (var a in s.Audios)
        {
            var rel = (a.Device is null ? "" : a.Device + "/") + a.Name.Replace(' ', '_') + ".wav";
            var path = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteSilentWav(path, s.Seconds);
            var t = new TrackInfo
            {
                Kind = TrackKinds.Audio, File = rel, TrackIndex = 0, Name = a.Name, Device = a.Device, StartOffsetMs = a.OffsetMs,
                DurationMs = ms, Codec = "pcm_s16le", SampleRate = 44100, Channels = 1,
            };
            t.Id = TrackInfo.MakeId(rel, 0);
            sc.Tracks.Add(t);
            WritePeaks(TrackInfo.PeakPath(folder, t.Id), s.Seconds, a.Loud, rng, s.Dropouts?.Where(d => d.Track == index).ToArray());
            index++;
        }
        foreach (var p in s.PausesSec ?? []) { sc.Pauses.Add(new PauseInfo { AtMs = p * 1000 }); }
        foreach (var (track, from, to) in s.Dropouts ?? [])
        {
            sc.Dropouts.Add(new DropoutInfo { Track = sc.Tracks.Where(t => !t.IsVideo).ElementAt(track).Id, StartMs = from * 1000, EndMs = to * 1000 });
        }
        if (s.Videos.Length > 0) { WritePoster(Path.Combine(folder, TakeSidecar.PosterFileName), seed); }
        TakeSidecar.Save(folder, sc);
    }

    /// <summary>A valid 16-bit mono 44.1 kHz WAV of silence (the data is a sparse run of zeros).</summary>
    private static void WriteSilentWav(string path, int seconds)
    {
        var data = seconds * 44100L * 2;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8);
        w.Write((int)(36 + data));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(44100);
        w.Write(44100 * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write((int)data);
        w.Flush();
        fs.SetLength(44 + data);
    }

    private static void WritePeaks(string path, int seconds, double loud, Random rng, (int Track, double From, double To)[]? gaps)
    {
        using var w = new PeakWriter(path, 44100);
        var buf = new short[44100];
        var phase = rng.NextDouble() * 10;
        for (var sec = 0; sec < seconds; sec++)
        {
            var env = Math.Max(0, Math.Sin((sec + phase) * 0.45) * Math.Sin((sec + phase) * 0.11 + 1) + 0.12);
            var amp = Math.Clamp(env * 14000 * loud, 250, 30000);
            if (gaps is not null && gaps.Any(g => sec >= g.From && sec < g.To)) { amp = 0; }
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
        var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(320, 180), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var fb = bmp.Lock())
        {
            var data = new int[320 * 180];
            for (var y = 0; y < 180; y++)
            {
                for (var x = 0; x < 320; x++)
                {
                    data[y * 320 + x] = (0xFF << 24) | ((byte)(40 + 120 * x / 320 + seed * 9 % 60) << 16) | ((byte)(60 + 100 * y / 180) << 8) | (byte)(130 + 60 * (x + y) / 500);
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(data, 0, fb.Address, data.Length);
        }
        bmp.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
