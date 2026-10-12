using System.Collections.Concurrent;
using Plural.Core.Audio;
using Plural.Core.Platform;
using Plural.Core.Protocol;
using Plural.Core.Recording;
using Plural.Core.Session;

namespace Plural.Core.Tests;

/// <summary>A SmartMike link in memory: the test plays the mic (answers the handshake, sends reports, drops the link).</summary>
internal sealed class FakeMikeLink : Stream
{
    private readonly BlockingCollection<byte[]> _in = [];
    private readonly CancellationTokenSource _dead = new();
    private readonly FrameReader _reader = new();
    private readonly List<ReceivedFrame> _sent = [];

    /// <summary>Frames the app wrote on this link.</summary>
    public IReadOnlyList<ReceivedFrame> Sent { get { lock (_sent) { return [.. _sent]; } } }

    public bool Disposed => _dead.IsCancellationRequested;

    /// <summary>The mic sends bytes to the app.</summary>
    public void Inject(byte[] data) => _in.Add(data);

    /// <summary>The mic vanishes: the app reads end-of-stream.</summary>
    public void Kill() => _in.Add([]);

    public IEnumerable<ReceivedFrame> Commands(Command cmd) => Sent.Where(f => f.IsCommand && f.Command == cmd);

    public override int Read(byte[] buffer, int offset, int count)
    {
        byte[] chunk;
        try
        {
            chunk = _in.Take(_dead.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ObjectDisposedException(nameof(FakeMikeLink));
        }
        if (chunk.Length == 0)
        {
            return 0;
        }
        chunk.CopyTo(buffer, offset);
        return chunk.Length;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_dead.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(FakeMikeLink));
        }
        var frames = new List<ReceivedFrame>();
        lock (_sent)
        {
            _reader.Feed(buffer.AsSpan(offset, count), frames);
            _sent.AddRange(frames);
        }
        foreach (var f in frames)
        {
            if (f.Raw.Length == Frame.SessionOpen.Length && f.Type == Frame.TypeSession)
            {
                Inject(Frame.BuildCommand(Command.CurVersion, new byte[50], 0, 0));
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        _dead.Cancel();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Refuses connections on demand; every accepted connection is a <see cref="FakeMikeLink"/>.</summary>
internal sealed class FakeMikeBt : IBluetoothPlatform
{
    private int _refuse;
    private volatile bool _refuseAll;

    public List<FakeMikeLink> Links { get; } = [];
    public int Attempts { get; private set; }
    public FakeMikeLink Current => Links[^1];

    public IReadOnlyList<PairedDevice> GetPairedDevices() => [];

    /// <summary>Refuse the next <paramref name="count"/> connection attempts.</summary>
    public void Refuse(int count) => _refuse = count;

    public bool RefuseAll { get => _refuseAll; set => _refuseAll = value; }

    public Task<Stream> ConnectSppAsync(BluetoothAddress address, CancellationToken ct)
    {
        lock (Links)
        {
            Attempts++;
            if (_refuseAll || _refuse > 0)
            {
                if (_refuse > 0) { _refuse--; }
                return Task.FromException<Stream>(new IOException("Bluetooth device is off"));
            }
            var link = new FakeMikeLink();
            Links.Add(link);
            return Task.FromResult<Stream>(link);
        }
    }
}

/// <summary>Virtual time for <see cref="ReconnectPolicy"/>: delays advance the clock instantly (1 real ms each).</summary>
internal sealed class FakeReconnectClock
{
    private long _ticks;
    public ConcurrentQueue<TimeSpan> Delays { get; } = new();
    public DateTimeOffset Now => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <summary>Delays that are retry waits (not the 500 ms after connecting).</summary>
    public List<TimeSpan> Backoffs => [.. Delays.Where(d => d != TimeSpan.FromMilliseconds(500))];

    public ReconnectPolicy Policy() => new()
    {
        Delay = async (d, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            Delays.Enqueue(d);
            Interlocked.Add(ref _ticks, d.Ticks);
            await Task.Delay(1, ct).ConfigureAwait(false);
        },
        Now = () => Now,
        ClockSyncTimeout = TimeSpan.FromMilliseconds(30),
    };
}

[Collection("timing")]
public class SessionReconnectTests
{
    private sealed class NoAudio : IAudioPlatform
    {
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public IAudioSink Open(AudioOutputDevice device, IAudioSource source, int sampleRate) => throw new NotSupportedException();
    }

    private static readonly PairedDevice Mic = new(new BluetoothAddress(0x2836380D5E01UL), "SmartMike+");

    private static async Task Until(Func<bool> cond, double seconds = 10)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed.TotalSeconds > seconds) { throw new TimeoutException("Condition not met."); }
            await Task.Delay(5);
        }
    }

    private static (MikeHub Hub, MikeChannel Channel, MikeSession Session, FakeMikeBt Bt, FakeReconnectClock Clock, List<MikeState> States) NewMic()
    {
        var bt = new FakeMikeBt();
        var hub = new MikeHub(bt, new NoAudio());
        var ch = hub.Add(Mic);
        var clock = new FakeReconnectClock();
        var session = ch.Session!;
        session.Reconnect = clock.Policy();
        var states = new List<MikeState>();
        session.StateChanged += s => { lock (states) { states.Add(s.State); } };
        return (hub, ch, session, bt, clock, states);
    }

    [Fact]
    public async Task DroppedLink_ReconnectsOnTheSameSessionAndChannel()
    {
        var (hub, ch, session, bt, clock, states) = NewMic();
        await session.ConnectAsync();
        session.StartAudio(AudioQuality.Medium);
        Assert.Equal(MikeState.Streaming, session.State);
        var first = bt.Current;

        bt.Refuse(2);
        first.Kill();
        await Until(() => session.State == MikeState.Reconnecting);
        Assert.NotNull(session.ReconnectingSince);
        await Until(() => session.State == MikeState.Streaming);

        Assert.Same(session, ch.Session);
        Assert.Null(session.ReconnectingSince);
        Assert.Null(session.LastError);
        Assert.Equal(4, bt.Attempts); // initial + 2 refused + 1 accepted
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], clock.Backoffs);
        lock (states) { Assert.Contains(MikeState.Reconnecting, states); }

        // New link: handshake and AUDIO_BEGIN again, with the same quality.
        var second = bt.Current;
        Assert.NotSame(first, second);
        Assert.Single(second.Sent, f => f.Raw.Length == Frame.SessionOpen.Length);
        var begin = Assert.Single(second.Commands(Command.AudioBegin));
        Assert.Equal(Frame.ParamTlv((ushort)CodecPlugin.CoQCelt, (ushort)AudioQuality.Medium), begin.Payload.ToArray());
        Assert.True(first.Disposed);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task BackoffIs_1_2_4_10_Then_10()
    {
        var (hub, _, session, bt, clock, _) = NewMic();
        await session.ConnectAsync();
        session.StartAudio();
        bt.Refuse(6);
        bt.Current.Kill();
        await Until(() => session.State == MikeState.Reconnecting);
        await Until(() => session.State == MikeState.Ready || session.State == MikeState.Streaming);
        Assert.Equal([1, 2, 4, 10, 10, 10, 10], clock.Backoffs.Select(d => (int)d.TotalSeconds));
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task ReadyLink_ReconnectsToReady()
    {
        var (hub, _, session, bt, _, _) = NewMic();
        await session.ConnectAsync();
        bt.Current.Kill();
        await Until(() => bt.Links.Count == 2 && session.State == MikeState.Ready);
        Assert.Empty(bt.Current.Commands(Command.AudioBegin));
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task UserDisconnect_DoesNotReconnect()
    {
        var (hub, _, session, bt, clock, states) = NewMic();
        await session.ConnectAsync();
        session.StartAudio();
        await session.DisconnectAsync();
        await Task.Delay(100);
        Assert.Equal(MikeState.Disconnected, session.State);
        Assert.Equal(1, bt.Attempts);
        Assert.Empty(clock.Backoffs);
        lock (states) { Assert.DoesNotContain(MikeState.Reconnecting, states); }
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task DisconnectWhileReconnecting_StopsRetrying()
    {
        var (hub, _, session, bt, _, _) = NewMic();
        await session.ConnectAsync();
        session.StartAudio();
        bt.RefuseAll = true;
        bt.Current.Kill();
        await Until(() => session.State == MikeState.Reconnecting && bt.Attempts > 3);
        await session.DisconnectAsync();
        Assert.Equal(MikeState.Disconnected, session.State);
        Assert.Null(session.ReconnectingSince);
        var attempts = bt.Attempts;
        await Task.Delay(100);
        Assert.Equal(attempts, bt.Attempts);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task NotRecording_GivesUpAfterTwoMinutes()
    {
        var (hub, _, session, bt, clock, _) = NewMic();
        await session.ConnectAsync();
        session.StartAudio();
        Assert.False(hub.IsRecording);
        bt.RefuseAll = true;
        bt.Current.Kill();
        await Until(() => session.State == MikeState.Disconnected);
        Assert.NotNull(session.LastError);
        Assert.Null(session.ReconnectingSince);
        Assert.InRange(clock.Elapsed.TotalSeconds, 120, 135);
        Assert.Equal([1, 2, 4, 10], clock.Backoffs.Take(4).Select(d => (int)d.TotalSeconds));
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task WhileRecording_NeverGivesUp_AndRecoversLater()
    {
        var (hub, ch, session, bt, clock, _) = NewMic();
        await session.ConnectAsync();
        session.StartAudio();
        var root = Path.Combine(Path.GetTempPath(), "plural_recon_" + Guid.NewGuid().ToString("N"));
        hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Wav };
        hub.StartRecordingAll(root);
        Assert.True(session.KeepReconnecting!());
        try
        {
            bt.RefuseAll = true;
            bt.Current.Kill();
            await Until(() => clock.Elapsed > TimeSpan.FromMinutes(10));
            Assert.Equal(MikeState.Reconnecting, session.State);
            Assert.NotNull(session.ReconnectingSince);
            Assert.NotNull(ch.RecordingPath); // the take keeps its track

            bt.RefuseAll = false;
            await Until(() => session.State == MikeState.Streaming);
            Assert.True(hub.IsRecording);
            Assert.Same(session, ch.Session);
        }
        finally
        {
            await hub.StopRecordingAllAsync();
            await hub.DisposeAsync();
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
        Assert.False(session.KeepReconnecting!());
    }

    [Fact]
    public async Task SettingsWhileReconnecting_AreQueuedAndSentAfterwards()
    {
        var (hub, _, session, bt, _, _) = NewMic();
        await session.ConnectAsync();
        session.StartAudio();
        bt.RefuseAll = true;
        bt.Current.Kill();
        await Until(() => session.State == MikeState.Reconnecting);
        session.Set(MikeSetting.MicGain, 30);
        session.Set(MikeSetting.MicGain, 70); // latest wins
        Assert.Equal(70, session.Settings[MikeSetting.MicGain]);
        bt.RefuseAll = false;
        await Until(() => session.State == MikeState.Streaming);

        var (cmd, sub) = MikeSettingMap.Resolve(MikeSetting.MicGain);
        var writes = bt.Current.Commands(cmd).Select(f => f.Payload.ToArray()).ToList();
        var write = Assert.Single(writes);
        Assert.Equal(Frame.ParamTlv(sub, 70), write);

        // Not connected at all: still rejected cleanly.
        await session.DisconnectAsync();
        Assert.Throws<InvalidOperationException>(() => session.Set(MikeSetting.MicGain, 10));
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task SilentStreamingLink_TimesOutAndReconnects()
    {
        var (hub, _, session, bt, clock, _) = NewMic();
        session.Reconnect.LinkTimeout = TimeSpan.FromMilliseconds(200);
        await session.ConnectAsync();
        session.StartAudio();
        var first = bt.Current;
        await Until(() => bt.Links.Count == 2 && session.State == MikeState.Streaming, 15);
        Assert.NotSame(first, bt.Current);
        Assert.NotEmpty(clock.Backoffs);
        await hub.DisposeAsync();
    }

    // ------------------------------------------------------------------ buttons

    private static byte[] ButtonReport(ButtonEvent e) =>
        Frame.BuildCommand(Command.ButtonPressed, [0x00, 0x00, (byte)((ushort)e >> 8), (byte)(ushort)e], 1, 0);

    [Fact]
    public async Task SessionRaisesButtonPressedForEveryReport_AndLogsRawValue()
    {
        var (hub, _, session, bt, _, _) = NewMic();
        var events = new List<ButtonEvent>();
        var log = new List<string>();
        session.ButtonPressed += (_, e) => { lock (events) { events.Add(e); } };
        session.Log += (_, m) => { lock (log) { log.Add(m); } };
        await session.ConnectAsync();
        bt.Current.Inject(ButtonReport(ButtonEvent.Record));
        bt.Current.Inject(ButtonReport(ButtonEvent.MfbLongPress));
        bt.Current.Inject(ButtonReport(ButtonEvent.JackUnplugged));
        await Until(() => { lock (events) { return events.Count == 3; } });
        Assert.Equal([ButtonEvent.Record, ButtonEvent.MfbLongPress, ButtonEvent.JackUnplugged], events);
        Assert.False(session.HeadphonesPlugged);
        lock (log) { Assert.Contains(log, l => l.Contains("button report") && l.Contains("Record (1)")); }
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Hub_RecordButton_UnknownMappingDisabledSettingAndDebounce()
    {
        var (hub, ch, session, bt, _, _) = NewMic();
        long now = 10_000;
        hub.ButtonClockMs = () => Interlocked.Read(ref now);
        var pressed = new List<MikeChannel>();
        hub.RecordButtonPressed += (_, c) => { lock (pressed) { pressed.Add(c); } };
        await session.ConnectAsync();
        session.StartAudio();

        async Task Press(ButtonEvent e)
        {
            int before; lock (pressed) { before = pressed.Count; }
            var seen = new TaskCompletionSource();
            session.ButtonPressed += Handler;
            void Handler(MikeSession s, ButtonEvent ev) => seen.TrySetResult();
            bt.Current.Inject(ButtonReport(e));
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.ButtonPressed -= Handler;
            await Task.Delay(20); // the hub's handler runs after ours on the same thread
        }

        Assert.Equal(ButtonEvent.MfbSinglePress, hub.RecordButtonEvent);
        hub.RecordButtonEvent = null;
        await Press(ButtonEvent.Record);
        lock (pressed) { Assert.Empty(pressed); } // unknown mapping: nothing toggles

        hub.RecordButtonEvent = ButtonEvent.Record;
        await Press(ButtonEvent.VolumeUp);
        lock (pressed) { Assert.Empty(pressed); }

        await Press(ButtonEvent.Record);
        lock (pressed) { Assert.Same(ch, Assert.Single(pressed)); }

        Interlocked.Add(ref now, 500); // within 1 s: debounced
        await Press(ButtonEvent.Record);
        lock (pressed) { Assert.Single(pressed); }

        Interlocked.Add(ref now, 600); // 1.1 s after the accepted press
        await Press(ButtonEvent.Record);
        lock (pressed) { Assert.Equal(2, pressed.Count); }

        hub.ButtonsControlRecording = false;
        Interlocked.Add(ref now, 5000);
        await Press(ButtonEvent.Record);
        lock (pressed) { Assert.Equal(2, pressed.Count); }
        hub.ButtonsControlRecording = true;
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Hub_RecordButton_IgnoredWithinTwoSecondsAfterStop()
    {
        var (hub, _, session, bt, _, _) = NewMic();
        long now = 10_000;
        hub.ButtonClockMs = () => Interlocked.Read(ref now);
        hub.RecordButtonEvent = ButtonEvent.Record;
        var count = 0;
        hub.RecordButtonPressed += (_, _) => Interlocked.Increment(ref count);
        await session.ConnectAsync();
        session.StartAudio();
        var root = Path.Combine(Path.GetTempPath(), "plural_btn_" + Guid.NewGuid().ToString("N"));
        hub.RecordingSettings = new RecordingSettings { Format = RecordingFormat.Wav };
        hub.StartRecordingAll(root);
        try
        {
            await hub.StopRecordingAllAsync();
            Interlocked.Add(ref now, 1500);
            bt.Current.Inject(ButtonReport(ButtonEvent.Record));
            await Task.Delay(150);
            Assert.Equal(0, Volatile.Read(ref count)); // 1.5 s after Stop: ignored

            Interlocked.Add(ref now, 600);
            bt.Current.Inject(ButtonReport(ButtonEvent.Record));
            await Until(() => Volatile.Read(ref count) == 1);
        }
        finally
        {
            await hub.DisposeAsync();
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    private const System.Reflection.BindingFlags NonPublic = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    [Fact]
    public async Task LateFailAfterUserDisconnect_DoesNotReconnectOrFail()
    {
        var (hub, _, session, bt, clock, states) = NewMic();
        await session.ConnectAsync();
        session.StartAudio();
        await session.DisconnectAsync();
        // a receive-thread Fail() that was already running while the user disconnected
        typeof(MikeSession).GetMethod("Fail", NonPublic)!.Invoke(session, ["connection closed by device"]);
        typeof(MikeSession).GetMethod("BeginReconnect", NonPublic)!.Invoke(session, ["late"]);
        await Task.Delay(100);
        Assert.Equal(MikeState.Disconnected, session.State);
        Assert.Equal(1, bt.Attempts);
        Assert.Empty(clock.Backoffs);
        lock (states) { Assert.DoesNotContain(MikeState.Reconnecting, states); Assert.DoesNotContain(MikeState.Failed, states); }
        // a new connect works again
        await session.ConnectAsync();
        Assert.Equal(MikeState.Ready, session.State);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task StateChangedIsNeverRaisedUnderTheReconnectLock()
    {
        var (hub, _, session, bt, _, _) = NewMic();
        var gate = typeof(MikeSession).GetField("_reconnectLock", NonPublic)!.GetValue(session)!;
        var underLock = new List<MikeState>();
        session.StateChanged += s => { if (Monitor.IsEntered(gate)) { lock (underLock) { underLock.Add(s.State); } } };
        await session.ConnectAsync();
        session.StartAudio();
        bt.Current.Kill();
        await Until(() => session.State == MikeState.Reconnecting);
        await Until(() => session.State == MikeState.Streaming);
        session.StopAudio();
        await session.DisconnectAsync();
        lock (underLock) { Assert.Empty(underLock); }
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task StartAudioRacingTheReconnect_NeverLeavesStreamingWithoutAudioBegin()
    {
        for (var round = 0; round < 15; round++)
        {
            var (hub, _, session, bt, _, _) = NewMic();
            await session.ConnectAsync();
            bt.Current.Kill();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3000)
            {
                try { session.StartAudio(); break; }
                catch (InvalidOperationException) { await Task.Yield(); }
            }
            await Until(() => session.State == MikeState.Streaming);
            // the link that is current once the session settles carries AUDIO_BEGIN
            await Until(() => session.State == MikeState.Streaming && bt.Current.Commands(Command.AudioBegin).Any(), 3);
            await hub.DisposeAsync();
        }
    }

    [Fact]
    public async Task HeartbeatRunsForAReadyLink_AlsoAfterAReconnect()
    {
        var (hub, _, session, bt, _, _) = NewMic();
        await session.ConnectAsync();
        static bool Beats(FakeMikeLink l) => l.Sent.Count(f => f.Type == Frame.TypeHeartbeat) >= 2;
        await Until(() => Beats(bt.Current), 3);
        bt.Current.Kill();
        await Until(() => bt.Links.Count == 2 && session.State == MikeState.Ready);
        await Until(() => Beats(bt.Current), 3);
        await hub.DisposeAsync();
    }
}
