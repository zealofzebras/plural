using Plural.Core.Audio;
using Plural.Core.Library;
using Plural.Core.Media;
using Plural.Core.Platform;
using Plural.Core.Recording;
using Plural.Core.Protocol;
using Plural.Core.Session;

namespace Plural.Core.Tests;

/// <summary>ClockLockedTrack with pauses: paused time removed, cut at the capture instant, no silence, dropouts in track-local time.</summary>
public class TrackPauseTests
{
    private const long T0 = 5_000_000;

    private sealed class Sim
    {
        public readonly List<short> Out = [];
        public readonly ClockLockedTrack Track;
        public long Now = T0;

        public Sim() => Track = new ClockLockedTrack(T0, () => Now, c => Out.AddRange(c));

        private static long At(double s) => T0 + (long)(s * 1e7);

        /// <summary>Ramp source (value = index / 80), packets of 512 after <paramref name="latency"/>; (time, pause?) events are called live when the
        /// simulated clock passes them, or all up front when <paramref name="scheduled"/>. The take stops at <paramref name="seconds"/>.</summary>
        public void Run(double srcRate, double seconds, double latency, (double T, bool Pause)[] events, bool scheduled = false,
            (double A, double B)? drop = null)
        {
            const int packet = 512;
            var next = 0;
            void Fire(int i)
            {
                if (events[i].Pause) { Track.Pause(At(events[i].T)); } else { Track.Resume(At(events[i].T)); }
            }
            if (scheduled) { for (var i = 0; i < events.Length; i++) { Fire(i); } next = events.Length; }
            var endSet = false;
            for (var k = 0; ; k++)
            {
                var capEnd = (k + 1) * packet / srcRate;
                var arrival = capEnd + latency;
                if (arrival >= seconds + latency + 0.05) { break; }
                Now = At(arrival);
                while (next < events.Length && events[next].T <= arrival) { Fire(next++); }
                if (!endSet && arrival >= seconds)
                {
                    // the recorder stops at `seconds`, then keeps receiving for Latency
                    while (next < events.Length && events[next].T <= seconds) { Fire(next++); }
                    Track.SetEnd(At(seconds));
                    endSet = true;
                }
                if (drop is { } d && capEnd > d.A && capEnd <= d.B) { continue; }
                var data = new short[packet];
                for (var i = 0; i < packet; i++) { data[i] = (short)Math.Round((k * packet + i) / 80.0); }
                Track.Write(data, 44100, Now);
            }
            if (!endSet) { Track.SetEnd(At(seconds)); }
            Now = At(seconds + latency + 0.1);
            Track.Finish(Now);
        }

        /// <summary>Source time (s) of the sample at output index n (average of a short window), from the ramp value.</summary>
        public double SourceTime(int n, double srcRate)
        {
            var avg = 0.0;
            for (var i = -50; i < 50; i++) { avg += Out[n + i]; }
            return avg / 100 * 80 / srcRate;
        }
    }

    /// <summary>How far the content at recorded position <paramref name="t"/> s is from where it should be (source time = t + shift).</summary>
    private static double Off(Sim sim, double t, double shift, double srcRate = 44100) => sim.SourceTime((int)(t * 44100), srcRate) - t - shift;

    [Theory]
    [InlineData(0.05, false)]
    [InlineData(0.3, false)]
    [InlineData(0.12, true)]
    [InlineData(0.3, true)]
    public void PausedTimeIsRemoved_CutAtTheCaptureInstant(double latency, bool scheduled)
    {
        var sim = new Sim();
        sim.Run(44100, 25.0, latency, [(15.0, true), (18.0, false)], scheduled);
        Assert.InRange(sim.Out.Count, 22 * 44100 - 1, 22 * 44100 + 1);
        Assert.Equal(0, sim.Track.Gaps);
        Assert.Equal(0, sim.Track.SilenceSamples);
        Assert.Empty(sim.Track.Dropouts);
        // before the pause output position == source time; after, the content is shifted by exactly the 3 s pause
        var before = Off(sim, 14.8, 0);
        Assert.True(Math.Abs(before) < 0.04, $"before {before}"); // the controller's own steady offset (packet size + smoothing), unchanged by this work
        foreach (var t in new[] { 15.3, 17, 21.5 })
        {
            var after = Off(sim, t, 3.0);
            Assert.True(Math.Abs(after - before) < 0.006, $"after at {t}: {after}, before {before}");
        }
        // the cut sits at the capture instant of the pause (output sample 15 s), whatever the bluetooth latency
        var cut = -1;
        for (var i = 14 * 44100; i < 16 * 44100; i++)
        {
            if (sim.Out[i] * 80.0 / 44100 > i / 44100.0 + 1.5) { cut = i; break; }
        }
        Assert.InRange(cut, 15 * 44100 - 60, 15 * 44100 + 60);
        // no silence around the cut
        Assert.DoesNotContain(sim.Out.Skip(15 * 44100 - 2000).Take(4000), v => v == 0);
    }

    [Fact]
    public void EarlyPause_StaysWithinStartupAlignmentError()
    {
        // inside the controller's start-up transient (first seconds) the cut is still accurate to about 12 ms
        var sim = new Sim();
        sim.Run(44100, 10.0, 0.12, [(4.0, true), (7.0, false)]);
        Assert.InRange(sim.Out.Count, 7 * 44100 - 1, 7 * 44100 + 1);
        Assert.Equal(0, sim.Track.Gaps);
        Assert.True(Math.Abs(Off(sim, 5.5, 3.0) - Off(sim, 3.9, 0)) < 0.014, $"{Off(sim, 5.5, 3.0)} vs {Off(sim, 3.9, 0)}");
    }

    [Fact]
    public void DriftingMic_PauseStillExact()
    {
        var sim = new Sim();
        const double rate = 44100 * 1.002;
        sim.Run(rate, 30.0, 0.15, [(15.0, true), (18.0, false), (21.0, true), (21.5, false)]);
        var expected = (30.0 - 3.5) * 44100;
        Assert.InRange(sim.Out.Count, expected - 1, expected + 1);
        Assert.Equal(0, sim.Track.Gaps);
        var before = Off(sim, 14.8, 0, rate);
        // recorded 25 s = source 28.5 s
        Assert.True(Math.Abs(Off(sim, 25, 3.5, rate) - before) < 0.008, $"{Off(sim, 25, 3.5, rate)} vs {before}");
    }

    [Fact]
    public void PauseShorterThanTheBuffer_AndBackToBack()
    {
        var sim = new Sim();
        sim.Run(44100, 25.0, 0.1, [(15.0, true), (15.02, false), (16.0, true), (16.01, false)]);
        Assert.InRange(sim.Out.Count, 25 * 44100 - 0.03 * 44100 - 1, 25 * 44100 - 0.03 * 44100 + 1);
        Assert.Equal(0, sim.Track.Gaps);
        Assert.True(Math.Abs(Off(sim, 20, 0.03) - Off(sim, 14.8, 0)) < 0.008);
    }

    [Fact]
    public void StopWhilePaused_EndsAtThePause()
    {
        var sim = new Sim();
        sim.Run(44100, 20.0, 0.2, [(15.0, true)]);
        Assert.InRange(sim.Out.Count, 15 * 44100 - 1, 15 * 44100 + 1);
        Assert.True(Math.Abs(Off(sim, 14.5, 0)) < 0.04);
        Assert.Equal(0, sim.Track.Gaps);
    }

    [Fact]
    public void PauseBeforeFirstPacket_DoesNotCrashOrPad()
    {
        var sim = new Sim();
        sim.Run(44100, 6.0, 0.1, [(0.5, true), (1.5, false)]);
        Assert.InRange(sim.Out.Count, 5 * 44100 - 1, 5 * 44100 + 1);
        Assert.Equal(0, sim.Track.Gaps);
    }

    [Fact]
    public void Dropout_IsRecordedInTrackLocalMilliseconds()
    {
        var sim = new Sim();
        // pause 2..4 s; the mic is away from capture time 8 s to 9 s -> recorded (track-local) 6 s to 7 s
        sim.Run(44100, 14.0, 0.1, [(2.0, true), (4.0, false)], drop: (8.0, 9.0));
        Assert.Equal(1, sim.Track.Gaps);
        var d = Assert.Single(sim.Track.Dropouts);
        Assert.InRange(d.StartSample / 44.1, 5900, 6150);
        Assert.InRange(d.EndSample / 44.1, 6950, 7200);
        Assert.InRange(sim.Out.Count, 12 * 44100 - 1, 12 * 44100 + 1);
        // silence really is there
        Assert.All(sim.Out.Skip(6 * 44100 + 5000).Take(30000), v => Assert.Equal(0, v));
    }

    [Fact]
    public void LinkDownInsideAPause_DoesNotEatAudioAfterTheResume()
    {
        var sim = new Sim();
        // pause 5..15 s, the mic is away from 7 s to 11 s (inside the pause): nothing after the resume may be discarded
        sim.Run(44100, 22.0, 0.1, [(5.0, true), (15.0, false)], drop: (7.0, 11.0));
        Assert.InRange(sim.Out.Count, 12 * 44100 - 1, 12 * 44100 + 1);
        Assert.Equal(0, sim.Track.Gaps);
        foreach (var t in new[] { 5.5, 7, 10, 11.5 })
        {
            var after = Off(sim, t, 10.0);
            Assert.True(Math.Abs(after) < 0.06, $"at {t}: {after}");
        }
    }

    [Fact]
    public void LinkDownAcrossTheResumeInstant_KeepsLaterAudioAligned_AndTheRestIsADropout()
    {
        var sim = new Sim();
        // pause 5..15 s, the mic is away from 7 s to 17 s: it is back 2 s after the resume
        sim.Run(44100, 22.0, 0.1, [(5.0, true), (15.0, false)], drop: (7.0, 17.0));
        Assert.InRange(sim.Out.Count, 12 * 44100 - 1, 12 * 44100 + 1);
        Assert.Equal(1, sim.Track.Gaps);
        var d = Assert.Single(sim.Track.Dropouts);
        Assert.InRange(d.StartSample / 44.1, 4900, 5300);   // recorded 5 s ...
        Assert.InRange(d.EndSample / 44.1, 6900, 7400);     // ... to 7 s
        Assert.All(sim.Out.Skip(5 * 44100 + 4000).Take(60000), v => Assert.Equal(0, v));
        foreach (var t in new[] { 8, 9.5, 11 })
        {
            var after = Off(sim, t, 10.0);
            Assert.True(Math.Abs(after) < 0.06, $"at {t}: {after}");
        }
    }
}

[Collection("timing")]
public class PauseTests
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

    private sealed class FakeAac : IAacEncoderFactory
    {
        public IAacEncoder Create(int sampleRate, int channels, int bitRate) => new Enc();

        private sealed class Enc : IAacEncoder
        {
            private int _pending;
            public int SampleRate => 44100;
            public int Channels => 1;
            public ReadOnlyMemory<byte> AudioSpecificConfig => new byte[] { 0x12, 0x08 };
            public int PrimingSamples => 0;
            public void Encode(ReadOnlySpan<short> pcm, Action<EncodedSample> output)
            {
                _pending += pcm.Length;
                while (_pending >= 1024) { _pending -= 1024; output(new EncodedSample(new byte[] { 1, 2, 3 }, 0, true)); }
            }
            public void Flush(Action<EncodedSample> output) { if (_pending > 0) { _pending = 0; output(new EncodedSample(new byte[] { 1, 2, 3 }, 0, true)); } }
            public void Dispose() { }
        }
    }

    /// <summary>30 fps camera; key frame every 30 frames, and (if <see cref="HonorRequest"/>) the next frame after a request.</summary>
    private sealed class FakeCamera : IVideoCapture
    {
        public CameraInfo Camera { get; } = new("0", "Fake", false);
        public int Width => 640;
        public int Height => 480;
        public int FrameRate => 30;
        public bool HonorRequest = true;
        public int KeyRequests;
        private volatile bool _requested;
        public event Action<PreviewFrame>? PreviewFrameReady { add { } remove { } }
        public event Action<EncodedSample>? SampleEncoded;
        private volatile bool _encoding;
        public void StartEncoding()
        {
            _encoding = true;
            Task.Run(async () =>
            {
                byte[] key = [0, 0, 0, 1, 0x67, 0x42, 0xC0, 0x1F, 0xDA, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80, 0, 0, 1, 0x65, 1, 2, 3];
                byte[] delta = [0, 0, 1, 0x41, 9, 9, 9];
                for (var i = 0; i < 1000 && _encoding; i++)
                {
                    var k = i % 30 == 0 || (HonorRequest && _requested);
                    _requested = false;
                    SampleEncoded?.Invoke(new EncodedSample(k ? key : delta, MediaClock.Now, k));
                    await Task.Delay(33);
                }
            });
        }
        public void StopEncoding() => _encoding = false;
        public void RequestKeyFrame() { Interlocked.Increment(ref KeyRequests); _requested = true; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVideo : IVideoPlatform
    {
        public IReadOnlyList<CameraInfo> GetCameras() => [];
        public Task<IVideoCapture> OpenAsync(CameraInfo camera, VideoSettings settings, CancellationToken ct = default) => Task.FromResult<IVideoCapture>(new FakeCamera());
    }

    private sealed class Mics
    {
        private readonly List<MikeSession> _sessions;
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private long _sent;
        public double Latency = 0.15;
        public Func<int, bool> Silent = _ => false;

        public Mics(List<MikeSession> sessions) => _sessions = sessions;

        private static void Raise(MikeSession s, short[] pcm)
        {
            var f = (PcmHandler?)typeof(MikeSession).GetField("PcmReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(s);
            f?.Invoke(s, pcm, 44100);
        }

        /// <summary>Runs the simulated mics for <paramref name="seconds"/> of wall time (data arrives <see cref="Latency"/> late).</summary>
        public async Task Run(double seconds)
        {
            var end = _sw.Elapsed.TotalSeconds + seconds;
            while (_sw.Elapsed.TotalSeconds < end)
            {
                var due = (long)((_sw.Elapsed.TotalSeconds - Latency) * 44100);
                while (_sent + 512 <= due)
                {
                    var pcm = new short[512];
                    for (var i = 0; i < 512; i++) { pcm[i] = (short)(9000 * Math.Sin((_sent + i) * 0.05)); }
                    for (var m = 0; m < _sessions.Count; m++)
                    {
                        if (!Silent(m)) { Raise(_sessions[m], pcm); }
                    }
                    _sent += 512;
                }
                await Task.Delay(5);
            }
        }
    }

    private static (MikeHub Hub, List<MikeSession> Sessions) NewHub(int mics)
    {
        var hub = new MikeHub(new FakeBt(), new FakeAudio(), new FakeAac(), new FakeVideo());
        var sessions = new List<MikeSession>();
        for (var i = 0; i < mics; i++)
        {
            var ch = hub.Add(new PairedDevice(new BluetoothAddress(0x2836380D5E00UL + (ulong)i), "SmartMike+"));
            typeof(MikeSession).GetProperty("State")!.SetValue(ch.Session!, MikeState.Streaming);
            sessions.Add(ch.Session!);
        }
        return (hub, sessions);
    }

    [Theory]
    [InlineData(RecordingFormat.Wav)]
    [InlineData(RecordingFormat.M4a)]
    [InlineData(RecordingFormat.Mp3)]
    [InlineData(RecordingFormat.Mka)]
    [InlineData(RecordingFormat.Mkv)]
    [InlineData(RecordingFormat.Mp4)]
    public async Task PauseResume_RemovesPausedTimeFromEveryTrack(RecordingFormat format)
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_pause_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(2);
            var camera = new FakeCamera();
            hub.Camera = camera;
            hub.RecordingSettings = new RecordingSettings { Format = format };
            var states = 0;
            hub.RecordingStateChanged += _ => Interlocked.Increment(ref states);
            var mics = new Mics(sessions) { Latency = 0.15 };

            var folder = hub.StartRecordingAll(root);
            Assert.False(hub.IsRecordingPaused);
            await mics.Run(1.0);
            hub.PauseRecording();
            Assert.True(hub.IsRecordingPaused);
            var frozen = hub.RecordedTime;
            await mics.Run(0.8);
            Assert.Equal(frozen, hub.RecordedTime);
            hub.PauseRecording(); // second pause is ignored
            hub.ResumeRecording();
            Assert.False(hub.IsRecordingPaused);
            await mics.Run(1.0);
            Assert.InRange(hub.RecordedTime.TotalSeconds, 1.95, 2.15);
            var take = await hub.StopRecordingAllAsync();

            Assert.Equal(4, states); // start, pause, resume, stop
            Assert.Empty(take.Errors);
            Assert.InRange(take.Duration.TotalSeconds, 1.95, 2.2);
            if (format.IsVideo()) { Assert.Equal(1, camera.KeyRequests); }

            var side = TakeSidecar.Load(folder)!;
            Assert.Equal(TakeStatus.Complete, side.Status);
            var p = Assert.Single(side.Pauses);
            Assert.InRange(p.AtMs, 940, 1150);
            Assert.Empty(side.Dropouts);
            foreach (var t in side.Tracks)
            {
                Assert.InRange(t.DurationMs, take.Duration.TotalMilliseconds - 70, take.Duration.TotalMilliseconds + 70);
            }

            // the files themselves: every audio track and the video are about 2 s, not 2.8 s
            foreach (var file in take.Files)
            {
                using var reader = MediaReader.Open(file);
                var lastTs = new Dictionary<int, long>();
                var maxGap = new Dictionary<int, long>();
                while (reader.TryRead(out var pkt))
                {
                    if (lastTs.TryGetValue(pkt.Track, out var prev)) { maxGap[pkt.Track] = Math.Max(maxGap.GetValueOrDefault(pkt.Track), pkt.Timestamp - prev); }
                    lastTs[pkt.Track] = pkt.Timestamp;
                }
                foreach (var tr in reader.Tracks)
                {
                    if (tr.Kind == MediaKind.Audio)
                    {
                        Assert.InRange(tr.Duration.TotalSeconds, 1.9, 2.25);
                    }
                    else
                    {
                        Assert.InRange(lastTs[tr.Index] / 1e7, 1.85, 2.2);          // last frame time on the shifted timeline
                        Assert.True(maxGap[tr.Index] < 0.25e7, $"video gap {maxGap[tr.Index] / 1e4:F0} ms"); // continuous across the pause
                    }
                }
            }
            if (format == RecordingFormat.Wav)
            {
                Assert.All(take.Files, f => Assert.InRange((new FileInfo(f).Length - 44) / 88200.0, 1.95, 2.2));
            }
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task Resume_WaitsForAKeyFrame_WhenTheCameraIgnoresTheRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_pause_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(1);
            var camera = new FakeCamera { HonorRequest = false };
            hub.Camera = camera;
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Mkv };
            var mics = new Mics(sessions);
            hub.StartRecordingAll(root);
            await mics.Run(0.5);
            hub.PauseRecording();
            await mics.Run(0.3);
            hub.ResumeRecording();
            await mics.Run(0.9); // the next natural key frame (every 30 frames = 1 s) is the first one after the resume
            var take = await hub.StopRecordingAllAsync();
            Assert.Equal(1, camera.KeyRequests);

            using var reader = MediaReader.Open(take.Files[0]);
            var video = new List<(long Ts, bool Key)>();
            while (reader.TryRead(out var pkt)) { if (pkt.Track == 0) { video.Add((pkt.Timestamp, pkt.KeyFrame)); } }
            // frames of the first second: frame 0 is the key frame; the resume happens at ~0.5 s, so frames until the key at frame 30 (1.0 s) are dropped
            Assert.True(video[0].Key);
            var gaps = video.Zip(video.Skip(1), (a, b) => (b.Ts - a.Ts, b.Key)).ToList();
            // The key-frame wait leaves the largest gap (≈ 0.5 s); scheduler jitter under load may add smaller ones.
            var wait = gaps.MaxBy(g => g.Item1);
            Assert.True(wait.Item1 > 0.3e7, $"expected a key-frame wait, largest gap {wait.Item1 / 1e4} ms");
            Assert.True(wait.Key, "the first frame after the wait is a key frame");
            Assert.All(video.Where((v, i) => i > 0 && gaps[i - 1].Item1 > 0.3e7), v => Assert.True(v.Key));
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 600 && !condition(); i++) { await Task.Delay(5); }
        Assert.True(condition());
    }

    [Fact]
    public async Task PausesAtTinyDelays_NeverCrashTheTimerThread()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_pause_" + Guid.NewGuid().ToString("N"));
        var unhandled = 0;
        UnhandledExceptionEventHandler h = (_, _) => Interlocked.Increment(ref unhandled);
        AppDomain.CurrentDomain.UnhandledException += h;
        try
        {
            var (hub, sessions) = NewHub(1);
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Wav };
            var mics = new Mics(sessions);
            hub.StartRecordingAll(root);
            var changes = 0;
            hub.RecordingStateChanged += _ => Interlocked.Increment(ref changes);
            await mics.Run(0.2);
            for (var i = 0; i < 150; i++)
            {
                // instants 0.1 .. 3 ms ahead: some run inline, some on a timer that may fire before it is registered
                hub.PauseRecording(MediaClock.Now + 1_000 + (i % 30) * 1_000);
                hub.ResumeRecording(MediaClock.Now + 1_000 + (i % 30) * 1_000);
            }
            await Task.Delay(300);
            await mics.Run(0.2);
            var take = await hub.StopRecordingAllAsync();
            Assert.NotNull(take);
            Assert.Equal(0, unhandled);
            Assert.True(changes > 0);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= h;
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task ScheduledPauseAndResume_AreAppliedAtTheirInstants()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_pause_" + Guid.NewGuid().ToString("N"));
        try
        {
            var (hub, sessions) = NewHub(1);
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Wav };
            var mics = new Mics(sessions);
            var events = new List<bool>();
            hub.StartRecordingAll(root);
            var start = hub.RecordingStartTime;
            hub.RecordingStateChanged += h => { lock (events) { events.Add(h.IsRecordingPaused); } };
            await mics.Run(0.5);
            var t = MediaClock.Now;
            hub.PauseRecording(t + 5_000_000);   // in 0.5 s
            hub.ResumeRecording(t + 10_000_000); // in 1.0 s
            Assert.False(hub.IsRecordingPaused);
            var feed = mics.Run(1.4); // the mics keep streaming during the whole pause
            await WaitFor(() => hub.IsRecordingPaused);
            Assert.InRange(MediaClock.Now - (t + 5_000_000), 0, 3_000_000); // reported when the instant was reached, not much later
            var frozen = hub.RecordedTime;
            Assert.InRange(frozen.TotalSeconds, (t + 5_000_000 - start) / 1e7 - 0.02, (t + 5_000_000 - start) / 1e7 + 0.02);
            await WaitFor(() => !hub.IsRecordingPaused);
            await feed;
            var tStop = MediaClock.Now;
            var take = await hub.StopRecordingAllAsync();
            // everything between the scheduled instants (0.5 s) is missing from the recording
            Assert.InRange(take.Duration.TotalSeconds, (tStop - start) / 1e7 - 0.5 - 0.05, (tStop - start) / 1e7 - 0.5 + 0.1);
            lock (events) { Assert.Equal([true, false, false], events); } // pause, resume, stop
            var side = TakeSidecar.Load(take.Folder)!;
            var at = (t + 5_000_000 - start) / 1e4;
            Assert.InRange(Assert.Single(side.Pauses).AtMs, at - 5, at + 5); // exact: the span is placed by its instant, not by when the timer ran
        }
        finally
        {
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task PauseAndResume_AreIgnoredWhenNotRecording()
    {
        var (hub, _) = NewHub(1);
        hub.PauseRecording();
        hub.ResumeRecording();
        hub.PauseRecording(MediaClock.Now + 1000);
        Assert.False(hub.IsRecordingPaused);
        Assert.Equal(TimeSpan.Zero, hub.RecordedTime);
        Assert.Equal(0, hub.RecordingBytesWritten);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Dropout_IsWrittenToTheSidecar_InTrackLocalTime_AndPeriodically()
    {
        var root = Path.Combine(Path.GetTempPath(), "plural_pause_" + Guid.NewGuid().ToString("N"));
        var oldInterval = TakeRecorder.FlushInterval;
        TakeRecorder.FlushInterval = TimeSpan.FromMilliseconds(250);
        try
        {
            var (hub, sessions) = NewHub(2);
            hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Mka };
            var mics = new Mics(sessions);
            var folder = hub.StartRecordingAll(root);
            await mics.Run(0.5);
            hub.PauseRecording();
            await mics.Run(0.5);
            hub.ResumeRecording();
            await mics.Run(1.0);
            mics.Silent = m => m == 0; // mic 1 keeps streaming, mic 0 is away for 1 s
            await mics.Run(1.0);
            mics.Silent = _ => false;
            await mics.Run(0.8);
            await Task.Delay(600); // the periodic writer has seen it before the stop
            var mid = TakeSidecar.Load(folder)!;
            Assert.Equal(TakeStatus.Recording, mid.Status);
            Assert.Single(mid.Dropouts);
            Assert.Single(mid.Pauses);
            Assert.True(hub.RecordingBytesWritten > 0);

            var take = await hub.StopRecordingAllAsync();
            var side = TakeSidecar.Load(folder)!;
            var d = Assert.Single(side.Dropouts);
            Assert.Equal(take.Tracks.First(t => !t.IsVideo).Id, d.Track);
            // recorded timeline: the mic went away 1.35 s into the recording (0.5 + 1.0 s of wall time, minus the 0.15 s latency; the pause is
            // not counted), for 1 s. The start is reported where the output stalled (up to the 60 ms backlog earlier).
            Assert.InRange(d.StartMs, 1200, 1500);
            Assert.InRange(d.EndMs, 2300, 2700);
            Assert.InRange(d.EndMs - d.StartMs, 900, 1300);
            Assert.InRange(Assert.Single(side.Pauses).AtMs, 440, 650);
        }
        finally
        {
            TakeRecorder.FlushInterval = oldInterval;
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }
}

public class RecordingEstimateTests
{
    [Fact]
    public void BytesPerSecond_PerFormat()
    {
        RecordingSettings S(RecordingFormat f, MatroskaAudioCodec c = MatroskaAudioCodec.Flac) => new() { Format = f, MatroskaAudio = c };
        Assert.Equal(2 * 88200, RecordingEstimate.BytesPerSecond(S(RecordingFormat.Wav), 2, null));
        Assert.Equal(88200 * 0.55 * 3, RecordingEstimate.BytesPerSecond(S(RecordingFormat.Mka), 3, null), 3);
        Assert.Equal(88200 * 3, RecordingEstimate.BytesPerSecond(S(RecordingFormat.Mka, MatroskaAudioCodec.Pcm), 3, null));
        Assert.Equal(192_000 / 8.0 * 2, RecordingEstimate.BytesPerSecond(S(RecordingFormat.M4a), 2, null));
        Assert.Equal(320_000 / 8.0, RecordingEstimate.BytesPerSecond(S(RecordingFormat.Mp3), 1, null));
        Assert.Equal(0, RecordingEstimate.BytesPerSecond(S(RecordingFormat.Wav), 0, null));
    }

    [Fact]
    public void BytesPerSecond_VideoAddsItsBitrate_OnlyForVideoFormats()
    {
        var v = new VideoSettings { BitRate = 8_000_000 };
        var mkv = new RecordingSettings { Format = RecordingFormat.Mkv };
        Assert.Equal(2 * 88200 * 0.55 + 1_000_000, RecordingEstimate.BytesPerSecond(mkv, 2, v), 3);
        var mp4 = new RecordingSettings { Format = RecordingFormat.Mp4, AacBitRate = 128_000 };
        Assert.Equal(16_000 + 1_000_000, RecordingEstimate.BytesPerSecond(mp4, 1, v));
        Assert.Equal(88200, RecordingEstimate.BytesPerSecond(new RecordingSettings { Format = RecordingFormat.Wav }, 1, v)); // audio-only format: no video
        // no bitrate set: the auto rule (1080p30 = 12 Mbps, 4K60 = 52.5 Mbps, 720p24 = 4.8 Mbps)
        var auto = new VideoSettings { BitRate = 0, Height = 1080, FrameRate = 30 };
        Assert.Equal(1_500_000.0, RecordingEstimate.BytesPerSecond(new RecordingSettings { Format = RecordingFormat.Mkv }, 0, auto));
        Assert.Equal(52_500_000, RecordingEstimate.AutoVideoBitRate(2160, 60));
        Assert.Equal(4_800_000, RecordingEstimate.AutoVideoBitRate(720, 24));
    }

    [Fact]
    public void TimeLeft_SubtractsTheReserve()
    {
        const long gb = 1024L * 1024 * 1024;
        var bps = RecordingEstimate.BytesPerSecond(new RecordingSettings(), 2, null);
        var left = RecordingEstimate.TimeLeft(10 * gb, bps);
        Assert.Equal((10 * gb - 200L * 1024 * 1024) / bps, left.TotalSeconds, 1);
        Assert.Equal(TimeSpan.Zero, RecordingEstimate.TimeLeft(100L * 1024 * 1024, bps));
        Assert.Equal(TimeSpan.FromSeconds(100), RecordingEstimate.TimeLeft(1100, 10, reserveBytes: 100));
        Assert.Equal(TimeSpan.MaxValue, RecordingEstimate.TimeLeft(gb, 0));
        Assert.Equal(TimeSpan.MaxValue, RecordingEstimate.TimeLeft(long.MaxValue, 1));
    }
}
