using System.Security.Cryptography;
using Plural.Core.Platform;
using Plural.Core.Recording;

namespace Plural.Core.Remote;

/// <summary>What the controlled app does on request. Calls arrive on background threads.</summary>
public interface IRemoteRecorder
{
    /// <summary>Current state, cheap and thread-safe (sent about twice a second).</summary>
    StatusMsg GetStatus();

    /// <summary>Asks the user whether <paramref name="controller"/> may control this device.</summary>
    Task<bool> ApproveAsync(HelloMsg controller);

    Task SetModeAsync(bool video);

    Task ConnectMicsAsync();

    /// <summary>Waits until <paramref name="startAt"/> (this device's MediaClock) and starts a take; returns its t0. Throws with a user-facing message.</summary>
    Task<long> StartAsync(string takeId, long startAt, bool video);

    /// <summary>Stops the take; the app then reports it through <see cref="RemoteResponder.TakeFinished"/>.</summary>
    Task StopAsync(string takeId);

    /// <summary>
    /// Pauses the running take at <paramref name="pauseAt"/> (this device's MediaClock; typically ~0.3 s ahead) and returns the
    /// instant applied. Only called for protocol-2 controllers; the default says pause is unsupported.
    /// </summary>
    Task<long> PauseAsync(string takeId, long pauseAt) =>
        Task.FromException<long>(new NotSupportedException("This app cannot pause a remote take."));

    /// <summary>Resumes the paused take at <paramref name="resumeAt"/> (this device's MediaClock); returns the instant applied.</summary>
    Task<long> ResumeAsync(string takeId, long resumeAt) =>
        Task.FromException<long>(new NotSupportedException("This app cannot pause a remote take."));

    /// <summary>The controlling device changed: connected, lost (still listed) or gone (null).</summary>
    void ControllerChanged(RemoteControllerInfo? controller);
}

public sealed record RemoteControllerInfo(string Id, string Name, bool Connected);

/// <summary>
/// The controlled side: accepts one controller at a time (after the user allows it), answers clock pings, reports status,
/// starts and stops takes, and sends each finished take's files until the controller acknowledges them.
/// A take keeps recording if the controller disappears; its files are sent when that controller reconnects.
/// </summary>
public sealed class RemoteResponder
{
    private readonly RemoteNetwork _net;
    private readonly IRemoteRecorder _recorder;
    private readonly object _lock = new();
    private readonly HashSet<string> _allowedThisSession = [];
    private readonly List<PendingTake> _pending = [];
    private Connection? _current;
    private RemoteControllerInfo? _controller;
    private (string TakeId, string ControllerId, long T0)? _active;

    private sealed record PendingTake(string ControllerId, string TakeId, RecordedTake Take, long T0);

    /// <summary>The controlling device (connected or temporarily lost), or null.</summary>
    public RemoteControllerInfo? Controller => _controller;

    /// <summary>Progress (0…1) of files being sent to the controller, or null.</summary>
    public double? SendProgress { get; private set; }

    /// <summary>Takes recorded for a controller that has not received them yet.</summary>
    public int PendingTakes { get { lock (_lock) { return _pending.Count; } } }

    /// <summary>True while a take was started by a controller (the local Record button is then the controller's).</summary>
    public bool HasRemoteTake => _active is not null;

    /// <summary>The controller's take being recorded here, or null.</summary>
    public string? ActiveTakeId => _active?.TakeId;

    internal RemoteResponder(RemoteNetwork net, IRemoteRecorder recorder)
    {
        _net = net;
        _recorder = recorder;
    }

    private sealed class Connection(RemoteLink link, HelloMsg hello)
    {
        public RemoteLink Link => link;
        public HelloMsg Hello => hello;
        public SemaphoreSlim Sending { get; } = new(1, 1);
        public Timer? StatusTimer { get; set; }
    }

    internal async Task HandleAsync(Stream stream, string transport)
    {
        var link = new RemoteLink(stream, transport);
        Connection? conn = null;
        link.Received += (l, kind, payload) =>
        {
            if (conn is { } c && ReferenceEquals(_current, c))
            {
                OnMessage(c, kind, payload);
            }
        };
        HelloMsg hello;
        try
        {
            hello = await RemoteLink.ExchangeHelloAsync(link, _net.Hello(wantsControl: false), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            link.Close();
            return;
        }
        if (!hello.WantsControl)
        {
            // A discovery probe: it hangs up after reading our Hello.
            await link.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => link.Close()).ConfigureAwait(false);
            return;
        }
        string? busy = null;
        lock (_lock)
        {
            if (_current is { } other && other.Hello.Id != hello.Id)
            {
                busy = $"{_net.Identity.Name} is already controlled by {other.Hello.Name}.";
            }
        }
        if (busy is null && _net.IsControlling)
        {
            busy = $"{_net.Identity.Name} is controlling other devices.";
        }
        if (busy is null && !_net.AllowControl)
        {
            busy = $"{_net.Identity.Name} does not accept remote control right now.";
        }
        bool allowed;
        if (busy is not null)
        {
            allowed = false;
        }
        else if (_allowedThisSession.Contains(hello.Id))
        {
            allowed = true;
        }
        else
        {
            try
            {
                allowed = await _recorder.ApproveAsync(hello).WaitAsync(TimeSpan.FromSeconds(80)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                allowed = false;
            }
        }
        if (!allowed || link.IsClosed)
        {
            try { await link.SendAsync(RemoteMsg.Reject, new RejectMsg(busy ?? $"{_net.Identity.Name} did not allow remote control.")).ConfigureAwait(false); }
            catch (Exception) { /* gone */ }
            await Task.Delay(300).ConfigureAwait(false);
            link.Close();
            return;
        }

        conn = new Connection(link, hello);
        Connection? replaced;
        lock (_lock)
        {
            _allowedThisSession.Add(hello.Id);
            replaced = _current;
            _current = conn;
        }
        replaced?.Link.Close(); // same controller reconnecting over another transport
        try
        {
            await link.SendAsync(RemoteMsg.Accept).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Drop(conn);
            return;
        }
        SetController(new RemoteControllerInfo(hello.Id, hello.Name, true));
        conn.StatusTimer = new Timer(_ => SendStatus(conn), null, 0, 500);
        _ = SendPendingAsync(conn);
        await link.Completion.ConfigureAwait(false);
        Drop(conn);
    }

    private void Drop(Connection conn)
    {
        conn.StatusTimer?.Dispose();
        bool wasCurrent;
        lock (_lock)
        {
            wasCurrent = ReferenceEquals(_current, conn);
            if (wasCurrent)
            {
                _current = null;
            }
        }
        if (wasCurrent)
        {
            SendProgress = null;
            // Keep showing the controller while a take or files are outstanding: it is expected back.
            var keep = _active is not null || PendingTakes > 0;
            SetController(keep ? new RemoteControllerInfo(conn.Hello.Id, conn.Hello.Name, false) : null);
        }
    }

    private void SetController(RemoteControllerInfo? c)
    {
        _controller = c;
        _recorder.ControllerChanged(c);
    }

    /// <summary>Ends remote control from this side (and forgets the approval for this session).</summary>
    public void Disconnect()
    {
        Connection? c;
        lock (_lock)
        {
            c = _current;
            if (c is not null)
            {
                _allowedThisSession.Remove(c.Hello.Id);
            }
        }
        c?.Link.Close();
        if (c is null && _active is null)
        {
            SetController(null);
        }
    }

    private void SendStatus(Connection c)
    {
        if (c.Link.IsClosed)
        {
            return;
        }
        try
        {
            _ = c.Link.SendAsync(RemoteMsg.Status, _recorder.GetStatus()).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        catch (Exception)
        {
            // closing
        }
    }

    private void OnMessage(Connection c, RemoteMsg kind, ReadOnlyMemory<byte> payload)
    {
        switch (kind)
        {
            case RemoteMsg.Ping:
                var ping = RemoteLink.Parse<PingMsg>(payload);
                _ = c.Link.SendAsync(RemoteMsg.Pong, new PongMsg(ping.T1, MediaClock.Now)).ContinueWith(_ => { }, TaskScheduler.Default);
                break;
            case RemoteMsg.SetMode:
                var mode = RemoteLink.Parse<SetModeMsg>(payload);
                _ = Run(() => _recorder.SetModeAsync(mode.Video));
                break;
            case RemoteMsg.ConnectMics:
                _ = Run(_recorder.ConnectMicsAsync);
                break;
            case RemoteMsg.Start:
                _ = StartAsync(c, RemoteLink.Parse<StartMsg>(payload));
                break;
            case RemoteMsg.Stop:
                var stop = RemoteLink.Parse<StopMsg>(payload);
                if (_active?.TakeId == stop.TakeId)
                {
                    _ = Run(() => _recorder.StopAsync(stop.TakeId));
                }
                break;
            case RemoteMsg.Pause:
                var pause = RemoteLink.Parse<PauseMsg>(payload);
                _ = PauseResumeAsync(c, pause.TakeId, pause.At, pause: true);
                break;
            case RemoteMsg.Resume:
                var resume = RemoteLink.Parse<ResumeMsg>(payload);
                _ = PauseResumeAsync(c, resume.TakeId, resume.At, pause: false);
                break;
            case RemoteMsg.TakeAck:
                var ack = RemoteLink.Parse<TakeAckMsg>(payload);
                lock (_lock) { _pending.RemoveAll(p => p.TakeId == ack.TakeId); }
                if (_current is null && _active is null && PendingTakes == 0)
                {
                    SetController(null);
                }
                break;
        }
    }

    private async Task PauseResumeAsync(Connection c, string takeId, long at, bool pause)
    {
        PausedMsg reply;
        try
        {
            if (_active?.TakeId != takeId)
            {
                throw new InvalidOperationException("Not recording this take.");
            }
            var applied = await (pause ? _recorder.PauseAsync(takeId, at) : _recorder.ResumeAsync(takeId, at)).ConfigureAwait(false);
            reply = new PausedMsg(takeId, pause, applied, null);
        }
        catch (Exception ex)
        {
            reply = new PausedMsg(takeId, pause, 0, ex.Message);
        }
        try { await c.Link.SendAsync(RemoteMsg.Paused, reply).ConfigureAwait(false); }
        catch (Exception) { /* lost; Status.Paused tells the controller after reconnecting */ }
    }

    /// <summary>
    /// A mic button on this device asked to toggle recording. While a controller is connected the press is sent to it as a
    /// <c>RecordRequest</c> (it starts or stops everyone) and the app must <b>not</b> record by itself: returns true.
    /// Returns false when nobody controls this device (or the controller predates protocol 2 and no take is running): the app
    /// then handles the press as its own Record button. During a controller's take whose link is down the press is swallowed (true).
    /// </summary>
    public bool ForwardRecordButton(string? source = null)
    {
        Connection? c;
        lock (_lock) { c = _current; }
        if (c is not null && !c.Link.IsClosed && c.Hello.EffectiveLevel >= RemoteProtocol.RecordRequestVersion)
        {
            _ = c.Link.SendAsync(RemoteMsg.RecordRequest, new RecordRequestMsg(source)).ContinueWith(_ => { }, TaskScheduler.Default);
            return true;
        }
        return _active is not null;
    }

    private static async Task Run(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception) { /* shown in the app's own UI */ }
    }

    private async Task StartAsync(Connection c, StartMsg m)
    {
        StartedMsg reply;
        try
        {
            if (_active is not null)
            {
                throw new InvalidOperationException("Already recording.");
            }
            var t0 = await _recorder.StartAsync(m.TakeId, m.StartAt, m.Video).ConfigureAwait(false);
            _active = (m.TakeId, c.Hello.Id, t0);
            reply = new StartedMsg(m.TakeId, t0, _recorder.GetStatus().Format, null);
        }
        catch (Exception ex)
        {
            reply = new StartedMsg(m.TakeId, 0, null, ex.Message);
        }
        try { await c.Link.SendAsync(RemoteMsg.Started, reply).ConfigureAwait(false); }
        catch (Exception) { /* lost; the status loop tells the controller after reconnecting */ }
    }

    /// <summary>
    /// The app calls this after any stop of a take that a controller started (by the controller or locally):
    /// the files are queued for that controller and sent now if it is connected.
    /// </summary>
    public void TakeFinished(RecordedTake take)
    {
        if (_active is not { } a)
        {
            return;
        }
        _active = null;
        Connection? c;
        lock (_lock)
        {
            _pending.Add(new PendingTake(a.ControllerId, a.TakeId, take, a.T0));
            c = _current;
        }
        if (c is not null && c.Hello.Id == a.ControllerId)
        {
            _ = SendPendingAsync(c);
        }
    }

    private async Task SendPendingAsync(Connection c)
    {
        await c.Sending.WaitAsync().ConfigureAwait(false);
        try
        {
            while (!c.Link.IsClosed)
            {
                PendingTake? p;
                lock (_lock) { p = _pending.FirstOrDefault(x => x.ControllerId == c.Hello.Id); }
                if (p is null)
                {
                    return;
                }
                await SendTakeAsync(c, p).ConfigureAwait(false);
                // Wait for the acknowledgement before the next take (resent after a reconnect otherwise).
                for (var i = 0; i < 600 && !c.Link.IsClosed; i++)
                {
                    lock (_lock)
                    {
                        if (!_pending.Contains(p)) { break; }
                    }
                    await Task.Delay(100).ConfigureAwait(false);
                }
                lock (_lock)
                {
                    if (_pending.Contains(p)) { return; }
                }
            }
        }
        catch (Exception)
        {
            // link lost: resent on reconnect
        }
        finally
        {
            SendProgress = null;
            c.Sending.Release();
        }
    }

    /// <summary>Test hook: the name announced for a file (default: its file name), to simulate older remotes.</summary>
    internal Func<string, string>? WireNameOverride { get; set; }

    private string WireName(string path) => WireNameOverride?.Invoke(path) ?? Path.GetFileName(path);

    private async Task SendTakeAsync(Connection c, PendingTake p)
    {
        var files = p.Take.Files.Where(File.Exists).ToList();
        var infos = files.Select(f => new RemoteFile(WireName(f), new FileInfo(f).Length)).ToList();
        var total = Math.Max(1, infos.Sum(f => f.Size));
        long sent = 0;
        SendProgress = 0;
        await c.Link.SendAsync(RemoteMsg.TakeReady,
            new TakeReadyMsg(p.TakeId, infos, p.Take.Duration.TotalSeconds, p.Take.Errors, p.T0)).ConfigureAwait(false);
        var buf = new byte[RemoteProtocol.FileChunk];
        foreach (var f in files)
        {
            await c.Link.SendAsync(RemoteMsg.FileBegin, new FileBeginMsg(p.TakeId, WireName(f), new FileInfo(f).Length)).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            {
                int n;
                while ((n = await fs.ReadAsync(buf).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buf, 0, n);
                    await c.Link.SendRawAsync(RemoteMsg.FileData, buf.AsMemory(0, n)).ConfigureAwait(false);
                    sent += n;
                    SendProgress = (double)sent / total;
                }
            }
            await c.Link.SendAsync(RemoteMsg.FileEnd, new FileEndMsg(p.TakeId, WireName(f), Convert.ToHexString(hash.GetHashAndReset()))).ConfigureAwait(false);
        }
    }
}
