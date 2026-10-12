using Plural.Core.Audio;
using Plural.Core.Library;
using Plural.Core.Media;
using Plural.Core.Platform;
using Plural.Core.Playback;

namespace Plural.Core.Tests;

internal sealed class FakeSink(AudioOutputDevice device, IAudioSource source) : IAudioSink
{
    public AudioOutputDevice Device { get; } = device;
    public IAudioSource Source { get; } = source;
    public TimeSpan Latency { get; set; }
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;

    public float[] Pull(int frames)
    {
        var b = new float[frames * 2];
        Source.Read(b);
        return b;
    }
}

internal sealed class FakeAudioPlatform : IAudioPlatform
{
    public List<AudioOutputDevice> Devices { get; } = [new("0", "Speakers")];
    public TimeSpan Latency { get; set; }
    public FakeSink? Sink { get; private set; }
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => Devices;

    public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate)
    {
        Assert.Equal(44100, sampleRate);
        Assert.Equal(2, source.Channels);
        return Sink = new FakeSink(device, source) { Latency = Latency };
    }
}

/// <summary>H.264 decoder stand-in: output is delayed by <see cref="Delay"/> frames, the output buffer is reused (like MediaCodec's ring), and after Flush only a key frame is accepted.</summary>
internal sealed class FakeVideoDecoder : IVideoDecoder
{
    public int Delay = 2;
    public int FlushCount;
    public bool Disposed;
    public readonly List<List<(long Ts, bool Key)>> Segments = [[]];
    private readonly Queue<long> _pending = new();
    private readonly byte[] _buf = new byte[4 * 4 * 4];
    private bool _needKey = true;

    public static int Number(long ts) => (int)Math.Round(ts * 30.0 / 10_000_000);

    public void Decode(ReadOnlySpan<byte> accessUnit, long timestamp, bool keyFrame, Action<DecodedVideoFrame> output)
    {
        if (_needKey && !keyFrame) { throw new InvalidOperationException("non-key frame after Flush"); }
        _needKey = false;
        lock (Segments) { Segments[^1].Add((timestamp, keyFrame)); }
        _pending.Enqueue(timestamp);
        while (_pending.Count > Delay) { Emit(_pending.Dequeue(), output); }
    }

    public void Drain(Action<DecodedVideoFrame> output)
    {
        while (_pending.Count > 0) { Emit(_pending.Dequeue(), output); }
    }

    public void Flush()
    {
        _pending.Clear();
        _needKey = true;
        FlushCount++;
        lock (Segments) { Segments.Add([]); }
    }

    private void Emit(long ts, Action<DecodedVideoFrame> output)
    {
        Array.Fill(_buf, (byte)0);
        _buf[0] = (byte)Number(ts);
        output(new DecodedVideoFrame(new PreviewFrame(_buf, 4, 4, 16), ts));
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeVideoFactory : IVideoDecoderFactory
{
    public readonly List<FakeVideoDecoder> Decoders = [];
    public bool CanDecode(string codec) => codec == "h264";

    public IVideoDecoder Create(string codec, ReadOnlyMemory<byte> codecPrivate, int width, int height, int maxSize)
    {
        var d = new FakeVideoDecoder();
        lock (Decoders) { Decoders.Add(d); }
        return d;
    }
}

public sealed class PlayerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plural_player_" + Guid.NewGuid().ToString("N"));
    private readonly List<TakePlayer> _players = [];

    public PlayerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var p in _players) { p.Dispose(); }
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static short[] Const(int n, short v) => Enumerable.Repeat(v, n).ToArray();

    private TrackInfo WavTrack(string name, short[] pcm, double offsetMs = 0, string? device = null)
    {
        var file = name + ".wav";
        File.WriteAllBytes(Path.Combine(_dir, file), MediaFixtures.Wav(pcm, 44100, 1));
        return new TrackInfo
        {
            Id = TrackInfo.MakeId(file, 0), Kind = TrackKinds.Audio, File = file, Name = name, Codec = "pcm_s16le", SampleRate = 44100,
            Channels = 1, StartOffsetMs = offsetMs, DurationMs = pcm.Length * 1000.0 / 44100, Device = device,
        };
    }

    private TakeInfo Take(TimeSpan duration, params TrackInfo[] tracks) => new()
    {
        Folder = _dir, Duration = duration, Tracks = tracks, Devices = ["Phone"], HasVideo = tracks.Any(t => t.IsVideo),
    };

    private (TakePlayer Player, FakeAudioPlatform Audio) Make(TakeInfo take, IVideoDecoderFactory? video = null, IAudioDecoderFactory? audio = null,
        TakeLibrary? library = null, TimeSpan? latency = null)
    {
        var platform = new FakeAudioPlatform { Latency = latency ?? TimeSpan.Zero };
        var p = new TakePlayer(take, platform, audio, video, library, 1280, backgroundClock: false);
        _players.Add(p);
        return (p, platform);
    }

    /// <summary>Pulls <paramref name="frames"/> from the sink in blocks, letting the feeders keep ahead (the real clock is not involved).</summary>
    private static (float[] L, float[] R) Pull(TakePlayer p, FakeAudioPlatform a, int frames)
    {
        var l = new float[frames];
        var r = new float[frames];
        for (var done = 0; done < frames;)
        {
            Assert.True(p.WaitForBuffers(TimeSpan.FromSeconds(10)), "buffers did not fill");
            var n = Math.Min(2048, frames - done);
            var b = a.Sink!.Pull(n);
            for (var i = 0; i < n; i++) { l[done + i] = b[2 * i]; r[done + i] = b[2 * i + 1]; }
            done += n;
        }
        return (l, r);
    }

    private static void AssertAll(float[] x, int from, int to, float expected, float tol = 1e-4f)
    {
        for (var i = from; i < to; i++) { Assert.True(Math.Abs(x[i] - expected) <= tol, $"sample {i}: {x[i]} != {expected}"); }
    }

    private const float Q = 1f / 32768f;

    // ------------------------------------------------------------------ mixer

    [Fact]
    public void Mixer_SumsTracksToBothChannels()
    {
        var take = Take(TimeSpan.FromSeconds(1), WavTrack("a", Const(44100, 8192)), WavTrack("b", Const(44100, 4096)));
        var (p, a) = Make(take);
        p.Play();
        var (l, r) = Pull(p, a, 4000);
        AssertAll(l, 0, 4000, 12288 * Q);
        AssertAll(r, 0, 4000, 12288 * Q);
        Assert.True(p.IsPlaying);
    }

    [Fact]
    public void Mixer_SoftClipsTheSum()
    {
        var take = Take(TimeSpan.FromSeconds(1), WavTrack("a", Const(44100, 30000)), WavTrack("b", Const(44100, 30000)));
        var (p, a) = Make(take);
        p.Play();
        var (l, _) = Pull(p, a, 2000);
        Assert.All(l, v => Assert.InRange(v, 0.9f, 1.0f));
    }

    [Fact]
    public void Mute_SilencesOnlyThatTrack()
    {
        var A = WavTrack("a", Const(44100, 8192));
        var B = WavTrack("b", Const(44100, 4096));
        var (p, a) = Make(Take(TimeSpan.FromSeconds(1), A, B));
        p.Play();
        Pull(p, a, 1000);
        p.SetMute(A.Id, true);
        var (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, 4096 * Q);
        Assert.True(p.Tracks.First(t => t.Id == A.Id).Mute);
        p.SetMute(A.Id, false);
        (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, 12288 * Q);
    }

    [Fact]
    public void Solo_OnlySoloedTracksPlay_AndSoloBeatsMute()
    {
        var A = WavTrack("a", Const(44100, 8192));
        var B = WavTrack("b", Const(44100, 4096));
        var C = WavTrack("c", Const(44100, 2048));
        var (p, a) = Make(Take(TimeSpan.FromSeconds(1), A, B, C));
        p.Play();

        p.SetSolo(A.Id, true);
        var (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, 8192 * Q);

        p.SetMute(A.Id, true); // solo wins over mute
        (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, 8192 * Q);

        p.SetSolo(C.Id, true); // two solos
        (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, (8192 + 2048) * Q);

        p.SetSolo(A.Id, false); // A is muted and no longer soloed; C stays
        (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, 2048 * Q);

        p.SetSolo(C.Id, false); // no solo: mute applies again
        (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, (4096 + 2048) * Q);
    }

    [Fact]
    public void Gain_IsInDecibels()
    {
        var A = WavTrack("a", Const(44100, 16000));
        var (p, a) = Make(Take(TimeSpan.FromSeconds(1), A));
        p.Play();
        p.SetGain(A.Id, -6.0206);
        var (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, 8000 * Q, 2e-4f);
        p.SetGain(A.Id, -20);
        (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, 1600 * Q, 2e-4f);
        p.SetGain(A.Id, -200); // clamped, effectively silent
        (l, _) = Pull(p, a, 2000);
        AssertAll(l, 1000, 2000, 0, 1e-4f);
    }

    [Fact]
    public void Levels_ArePerTrackAndPerDevice_EvenWhenMuted()
    {
        var A = WavTrack("a", Const(44100, 16384), device: "PhoneB");
        var B = WavTrack("b", Const(44100, 4096));
        var (p, a) = Make(Take(TimeSpan.FromSeconds(1), A, B));
        p.SetMute(A.Id, true);
        p.Play();
        Pull(p, a, 4000);
        Assert.Equal(0.5f, p.GetLevel(A.Id), 3);
        var dev = p.GetDeviceLevels();
        Assert.Equal(0.5f, dev["PhoneB"], 3);
        Assert.Equal(0.125f, dev["Phone"], 3); // the take's own device
        Assert.Equal(2, p.GetTrackLevels().Count);
    }

    // ------------------------------------------------------------------ timeline

    [Fact]
    public void Offsets_PlaceTracksOnACommonTimeline()
    {
        // B started 200 ms before the controller's timeline origin, A 500 ms after it: origin = B's start
        var click = new short[44100];
        click[100] = 8192;
        var early = new short[44100];
        early[50] = 16384;
        var A = WavTrack("a", click, offsetMs: 500);
        var B = WavTrack("b", early, offsetMs: -200);
        var take = Take(TimeSpan.FromMilliseconds(1700), A, B);
        var (p, a) = Make(take);
        Assert.Equal(TimeSpan.FromMilliseconds(700), p.Tracks.First(t => t.Id == A.Id).Offset);
        Assert.Equal(TimeSpan.Zero, p.Tracks.First(t => t.Id == B.Id).Offset);
        p.Play();
        var (l, r) = Pull(p, a, 40000);
        var hits = Enumerable.Range(0, l.Length).Where(i => Math.Abs(l[i]) > 0.01f).ToList();
        Assert.Equal([50, 30870 + 100], hits);
        Assert.Equal(0.5f, l[50], 4);
        Assert.Equal(0.25f, l[30970], 4);
        Assert.Equal(l, r);
    }

    [Fact]
    public void Seek_LandsWithinOneBlock_AndHoldsPositionUntilPlayed()
    {
        var pcm = Enumerable.Range(0, 30000).Select(i => (short)i).ToArray();
        var (p, a) = Make(Take(TimeSpan.FromSeconds(30000 / 44100.0), WavTrack("ramp", pcm)));
        p.Seek(TimeSpan.FromMilliseconds(200));
        Assert.Equal(TimeSpan.FromMilliseconds(200), p.Position);
        p.Play();
        var (l, _) = Pull(p, a, 600);
        Assert.InRange(l[0] * 32768, 8820 - 512, 8820 + 512);
        Assert.Equal(l[0] * 32768 + 1, l[1] * 32768, 0.5); // contiguous
        Assert.InRange(p.Position.TotalMilliseconds, 200 + 600 / 44.1 - 0.1, 200 + 600 / 44.1 + 0.1);

        // seek while playing, backwards and forwards
        p.Seek(TimeSpan.FromMilliseconds(100));
        (l, _) = Pull(p, a, 100);
        Assert.InRange(l[0] * 32768, 4410 - 512, 4410 + 512);
        p.Seek(TimeSpan.FromMilliseconds(500));
        (l, _) = Pull(p, a, 100);
        Assert.InRange(l[0] * 32768, 22050 - 512, 22050 + 512);
    }

    [Fact]
    public void Position_SubtractsOutputLatency()
    {
        var (p, a) = Make(Take(TimeSpan.FromSeconds(2), WavTrack("a", Const(88200, 1000))), latency: TimeSpan.FromMilliseconds(100));
        p.Play();
        Pull(p, a, 4410);
        Assert.Equal(TimeSpan.Zero, p.Position); // nothing audible yet
        Pull(p, a, 4410);
        Assert.Equal(100, p.Position.TotalMilliseconds, 0.5);
        p.Pause(); // resumes from what was heard
        Assert.Equal(100, p.Position.TotalMilliseconds, 0.5);
        Assert.False(p.IsPlaying);
        var silent = a.Sink!.Pull(1000);
        Assert.All(silent, v => Assert.Equal(0f, v));
        Assert.Equal(100, p.Position.TotalMilliseconds, 0.5);
        p.Play();
        var (l, _) = Pull(p, a, 100);
        Assert.Equal(1000 * Q, l[0], 4);
    }

    [Fact]
    public void Ended_FiresOnce_PausesAndPlayRestarts()
    {
        var pcm = Enumerable.Range(0, 4410).Select(i => (short)(1000 + i)).ToArray();
        var (p, a) = Make(Take(TimeSpan.FromMilliseconds(100), WavTrack("a", pcm)));
        var ended = 0;
        var states = 0;
        p.Ended += () => ended++;
        p.StateChanged += () => states++;
        p.Play();
        p.Tick();
        Assert.Equal(0, ended);
        Pull(p, a, 4000);
        p.Tick();
        Assert.Equal(0, ended);
        Pull(p, a, 1000);
        p.Tick();
        Assert.Equal(1, ended);
        Assert.False(p.IsPlaying);
        Assert.Equal(p.Duration, p.Position);
        p.Tick();
        Assert.Equal(1, ended);
        Assert.True(states >= 2);

        p.Play(); // restarts from 0
        var (l, _) = Pull(p, a, 10);
        Assert.Equal(1000 * Q, l[0], 4);
        Assert.True(p.IsPlaying);
    }

    [Fact]
    public void PositionChanged_IsRaisedWhilePlaying()
    {
        var (p, a) = Make(Take(TimeSpan.FromSeconds(2), WavTrack("a", Const(88200, 100))));
        var seen = new List<TimeSpan>();
        p.PositionChanged += seen.Add;
        p.Play();
        Pull(p, a, 8820);
        p.Tick();
        Assert.Contains(seen, t => Math.Abs(t.TotalMilliseconds - 200) < 1);
    }

    [Fact]
    public void NoOutputDevice_StillRunsOnATimer()
    {
        var platform = new FakeAudioPlatform();
        platform.Devices.Clear();
        var take = Take(TimeSpan.FromSeconds(5), WavTrack("a", Const(44100 * 5, 1000)));
        var p = new TakePlayer(take, platform);
        _players.Add(p);
        Assert.Null(platform.Sink);
        p.Play();
        Assert.Equal("No audio output device", p.OutputProblem);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (p.Position < TimeSpan.FromMilliseconds(150) && sw.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(10); }
        Assert.True(p.Position >= TimeSpan.FromMilliseconds(150), "timer clock did not advance");
        Assert.True(p.GetLevel(take.Tracks[0].Id) > 0.01f);
        p.Pause();
        var at = p.Position;
        Thread.Sleep(100);
        Assert.Equal(at, p.Position);
    }

    // ------------------------------------------------------------------ problems

    [Fact]
    public void UnplayableTrack_IsReported_OthersKeepPlaying()
    {
        var good = WavTrack("good", Const(44100, 8192));
        File.WriteAllBytes(Path.Combine(_dir, "lossy.m4a"), MediaFixtures.M4a(40));
        var aac = new TrackInfo { Id = "lossy.m4a#0", Kind = TrackKinds.Audio, File = "lossy.m4a", Name = "lossy", Codec = "aac", DurationMs = 900 };
        var missing = new TrackInfo { Id = "gone.wav#0", Kind = TrackKinds.Audio, File = "gone.wav", Name = "gone", Codec = "pcm_s16le", DurationMs = 900 };
        var (p, a) = Make(Take(TimeSpan.FromSeconds(1), good, aac, missing));
        var byId = p.Tracks.ToDictionary(t => t.Id);
        Assert.True(byId[good.Id].CanPlay);
        Assert.Null(byId[good.Id].Problem);
        Assert.False(byId[aac.Id].CanPlay);
        Assert.Contains("AAC", byId[aac.Id].Problem);
        Assert.False(byId[missing.Id].CanPlay);
        Assert.NotNull(byId[missing.Id].Problem);
        p.Play();
        var (l, _) = Pull(p, a, 3000);
        AssertAll(l, 0, 3000, 8192 * Q);
        p.SetMute(aac.Id, true); // harmless
        p.SetGain("nope", 3);
    }

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void Settings_PersistToTheSidecarAndLoadBack()
    {
        var root = Path.Combine(_dir, "root");
        var folder = Path.Combine(root, "20240102_030405");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.wav"), MediaFixtures.Wav(Const(44100, 8192), 44100, 1));
        File.WriteAllBytes(Path.Combine(folder, "b.wav"), MediaFixtures.Wav(Const(44100, 4096), 44100, 1));
        var lib = new TakeLibrary(root);
        var take = lib.Load(folder)!;
        Assert.Empty(take.Playback);
        var idA = take.Tracks[0].Id;
        var idB = take.Tracks[1].Id;

        var (p, _) = Make(take, library: lib);
        p.SetGain(idA, -6);
        p.SetMute(idB, true);
        p.SetSolo(idA, true);
        p.FlushSettings();

        var reloaded = lib.Load(folder)!;
        Assert.Equal(-6, reloaded.Playback[idA].GainDb);
        Assert.True(reloaded.Playback[idA].Solo);
        Assert.False(reloaded.Playback[idA].Mute);
        Assert.True(reloaded.Playback[idB].Mute);

        var (p2, _) = Make(reloaded, library: lib);
        var ta = p2.Tracks.First(t => t.Id == idA);
        var tb = p2.Tracks.First(t => t.Id == idB);
        Assert.Equal(-6, ta.GainDb);
        Assert.True(ta.Solo);
        Assert.True(tb.Mute);

        p2.SetGain(idA, 0);
        p2.SetSolo(idA, false);
        p2.SetMute(idB, false);
        p2.FlushSettings();
        Assert.Empty(lib.Load(folder)!.Playback); // all-default settings are removed
    }

    [Fact]
    public void Settings_AreWrittenOnDispose()
    {
        var root = Path.Combine(_dir, "root");
        var folder = Path.Combine(root, "20240102_030405");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.wav"), MediaFixtures.Wav(Const(44100, 8192), 44100, 1));
        var lib = new TakeLibrary(root);
        var take = lib.Load(folder)!;
        var (p, _) = Make(take, library: lib);
        p.SetMute(take.Tracks[0].Id, true);
        p.Dispose();
        Assert.True(lib.Load(folder)!.Playback[take.Tracks[0].Id].Mute);
    }

    // ------------------------------------------------------------------ video

    private (TakeInfo Take, TrackInfo Video, TrackInfo Audio) VideoTake()
    {
        var (file, _) = MediaFixtures.Mkv(3);
        File.WriteAllBytes(Path.Combine(_dir, "v.mkv"), file);
        using var r = MediaReader.Open(new MemoryStream(file));
        var v = r.Tracks.First(t => t.Kind == MediaKind.Video);
        var au = r.Tracks.First(t => t.Kind == MediaKind.Audio);
        var video = new TrackInfo
        {
            Id = TrackInfo.MakeId("v.mkv", v.Index), Kind = TrackKinds.Video, File = "v.mkv", TrackIndex = v.Index, Name = "Video",
            Codec = "h264", Width = 1280, Height = 720, DurationMs = 3000,
        };
        var audio = new TrackInfo
        {
            Id = TrackInfo.MakeId("v.mkv", au.Index), Kind = TrackKinds.Audio, File = "v.mkv", TrackIndex = au.Index, Name = "Mic A",
            Codec = "flac", SampleRate = 44100, Channels = 1, DurationMs = 3000,
        };
        return (Take(TimeSpan.FromSeconds(3), video, audio), video, audio);
    }

    private static void Collect(TakePlayer p, List<(string Id, int Number, int W)> into) =>
        p.FrameReady += (id, f) => { lock (into) { into.Add((id, f.Bgra.Span[0], f.Width)); } };

    [Fact]
    public void Video_FramesAreShownInOrderAsThePositionAdvances()
    {
        var (take, video, _) = VideoTake();
        var fac = new FakeVideoFactory();
        var (p, a) = Make(take, fac);
        var frames = new List<(string Id, int Number, int W)>();
        Collect(p, frames);
        p.Play();
        Assert.True(p.WaitForBuffers(TimeSpan.FromSeconds(10)));
        p.Tick();
        for (var i = 0; i < 60; i++)
        {
            Pull(p, a, 441);
            p.Tick();
        }
        Assert.All(frames, f => { Assert.Equal(video.Id, f.Id); Assert.Equal(4, f.W); });
        var numbers = frames.Select(f => f.Number).ToList();
        Assert.True(numbers.Count >= 17, $"only {numbers.Count} frames");
        Assert.Equal(Enumerable.Range(0, numbers.Count), numbers); // in order, nothing skipped at 10 ms ticks
        Assert.Equal(0, p.VideoFramesDropped);
        Assert.Single(fac.Decoders);
    }

    [Fact]
    public void Video_LateFramesAreDropped()
    {
        var (take, _, _) = VideoTake();
        var (p, a) = Make(take, new FakeVideoFactory());
        var frames = new List<(string Id, int Number, int W)>();
        Collect(p, frames);
        p.Play();
        Pull(p, a, 8820); // 200 ms without a tick: the queue only holds frames 0..5
        p.Tick();
        Assert.Single(frames);
        Assert.True(frames[0].Number >= 5, $"showed frame {frames[0].Number}");
        Assert.True(p.VideoFramesDropped >= 5);
        Pull(p, a, 2000);
        p.Tick();
        Assert.True(frames.Count >= 2);
        Assert.True(frames[^1].Number > frames[0].Number);
        Assert.Equal(frames.Select(f => f.Number).OrderBy(n => n), frames.Select(f => f.Number));
    }

    [Fact]
    public void Video_SeekStartsAtTheKeyFrame_ShowsTheTargetFrame_EvenWhenPaused()
    {
        var (take, _, _) = VideoTake();
        var fac = new FakeVideoFactory();
        var (p, _) = Make(take, fac);
        var frames = new List<(string Id, int Number, int W)>();
        Collect(p, frames);

        p.Seek(TimeSpan.FromMilliseconds(1520)); // frame 45 (1.5 s); key frames at 0 and 1.0 s
        Assert.True(p.WaitForBuffers(TimeSpan.FromSeconds(10)));
        p.Tick();
        Assert.False(p.IsPlaying);
        Assert.Equal([45], frames.Select(f => f.Number));
        var dec = fac.Decoders[0];
        List<(long Ts, bool Key)> seg;
        lock (dec.Segments) { seg = [.. dec.Segments[^1]]; }
        Assert.Equal((10_000_000L, true), seg[0]); // restarted at the previous key frame
        Assert.True(dec.FlushCount >= 1);
        Assert.DoesNotContain(frames, f => f.Number < 45);

        frames.Clear();
        p.Seek(TimeSpan.FromMilliseconds(510)); // frame 15, back to the first key frame
        Assert.True(p.WaitForBuffers(TimeSpan.FromSeconds(10)));
        p.Tick();
        Assert.Equal([15], frames.Select(f => f.Number));
        lock (dec.Segments) { seg = [.. dec.Segments[^1]]; }
        Assert.Equal((0L, true), seg[0]);
        p.Tick();
        Assert.Single(frames); // not shown twice
    }

    [Fact]
    public void Video_WithoutADecoder_IsReported_AudioStillPlays()
    {
        var (take, video, audio) = VideoTake();
        var (p, a) = Make(take, video: null);
        var reports = new List<(string Id, string Reason)>();
        p.VideoUnavailable += (id, reason) => reports.Add((id, reason));
        Assert.False(p.Tracks.First(t => t.Id == video.Id).CanPlay);
        Assert.NotNull(p.Tracks.First(t => t.Id == video.Id).Problem);
        Assert.True(p.Tracks.First(t => t.Id == audio.Id).CanPlay);
        p.Seek(TimeSpan.Zero);
        p.Tick();
        Assert.Single(reports);
        Assert.Equal(video.Id, reports[0].Id);
        p.Tick();
        Assert.Single(reports);
        p.Play();
        var (l, _) = Pull(p, a, 1000);
        Assert.Contains(l, v => Math.Abs(v) > 0.01f);
    }

    [Fact]
    public void Video_FactoryThatRejectsH264_IsReportedToo()
    {
        var (take, video, _) = VideoTake();
        var (p, _) = Make(take, new RejectingFactory());
        Assert.False(p.Tracks.First(t => t.Id == video.Id).CanPlay);
    }

    private sealed class RejectingFactory : IVideoDecoderFactory
    {
        public bool CanDecode(string codec) => false;
        public IVideoDecoder Create(string codec, ReadOnlyMemory<byte> codecPrivate, int width, int height, int maxSize) => throw new NotSupportedException();
    }

    [Fact]
    public void Video_DecoderFailure_IsReportedWithoutThrowing()
    {
        var (take, video, _) = VideoTake();
        var (p, _) = Make(take, new ThrowingFactory());
        var reports = new List<string>();
        p.VideoUnavailable += (id, reason) => reports.Add(id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (p.Tracks.First(t => t.Id == video.Id).CanPlay && sw.Elapsed < TimeSpan.FromSeconds(10)) { Thread.Sleep(5); }
        p.Seek(TimeSpan.Zero);
        p.Tick();
        Assert.Equal([video.Id], reports);
    }

    private sealed class ThrowingFactory : IVideoDecoderFactory
    {
        public bool CanDecode(string codec) => true;
        public IVideoDecoder Create(string codec, ReadOnlyMemory<byte> codecPrivate, int width, int height, int maxSize) => throw new InvalidOperationException("no hardware");
    }

    [Fact]
    public async Task DisposeAsync_ClosesTheSinkAndStopsThreads()
    {
        var (take, _, _) = VideoTake();
        var fac = new FakeVideoFactory();
        var (p, a) = Make(take, fac);
        p.Play();
        Pull(p, a, 1000);
        await p.DisposeAsync();
        Assert.True(a.Sink!.Disposed);
        Assert.True(fac.Decoders[0].Disposed);
        Assert.Throws<ObjectDisposedException>(p.Play);
    }
}
