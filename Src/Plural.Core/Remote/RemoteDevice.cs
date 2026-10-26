using System.Security.Cryptography;
using Plural.Core.Platform;

namespace Plural.Core.Remote;

public enum RemoteDeviceState
{
    Connecting,
    AwaitingApproval,
    Connected,
    Reconnecting,
    Rejected,
    Disconnected,
}

/// <summary>A finished take received from a remote: files are in <see cref="Folder"/>.</summary>
public sealed record RemoteTakeResult(
    string TakeId, string DeviceId, string DeviceName, string Folder, IReadOnlyList<string> Files, double Seconds,
    double? StartOffsetMs, double? UncertaintyMs, IReadOnlyList<string> Errors);

/// <summary>
/// Controller-side view of one remote app: keeps the link up (reconnecting through any transport that found it),
/// estimates the clock offset, starts/stops takes and receives their files. Events fire on background threads.
/// </summary>
public sealed class RemoteDevice : IAsyncDisposable
{
    private readonly RemoteNetwork _net;
    private readonly CancellationTokenSource _life = new();
    private readonly Task _run;
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposing;
    private readonly object _lock = new();
    private readonly List<(long Rtt, long Offset)> _samples = [];
    private readonly Dictionary<string, (double OffsetMs, double UncertaintyMs)> _startOffsets = [];
    private readonly Dictionary<string, TaskCompletionSource<StartedMsg>> _starting = [];
    private readonly Dictionary<string, TaskCompletionSource<RemoteTakeResult>> _takes = [];
    private readonly HashSet<string> _explicitDownloads = [];
    private readonly Dictionary<(string TakeId, bool Paused), TaskCompletionSource<PausedMsg>> _pausing = [];
    private List<RemoteEndpoint> _endpoints;
    private RemoteLink? _link;
    private Task? _ping;
    private Incoming? _incoming;

    public string Id { get; }
    public string Name { get; private set; }
    public string Platform { get; private set; }
    public RemoteDeviceState State { get; private set; } = RemoteDeviceState.Connecting;
    public string? Error { get; private set; }
    public StatusMsg? Status { get; private set; }
    public bool HasVideo { get; private set; }

    /// <summary>Protocol version from the remote's Hello (assumed current until it answers).</summary>
    public int Protocol { get; private set; } = RemoteProtocol.Version;

    /// <summary>False for a remote running an older Plural: it is never sent Pause and the UI shows "Doesn't support pause".</summary>
    public bool SupportsPause => Protocol >= RemoteProtocol.PauseVersion;

    /// <summary>Whether the connected peer supports independently requested, deferred downloads (level 3).</summary>
    public bool SupportsDeferredDownloads => Protocol >= RemoteProtocol.DeferredDownloadsVersion;

    /// <summary>The remote's mic button asked to toggle recording (also raised on <see cref="RemoteNetwork.RecordRequested"/>).</summary>
    public event Action<RemoteDevice>? RecordRequested;

    /// <summary>"Wi-Fi" or "Bluetooth" while connected (diagnostics only).</summary>
    public string? Transport => _link?.Transport;

    /// <summary>Remote clock minus controller clock (ticks), from the lowest-latency ping; null until measured.</summary>
    public long? ClockOffset { get; private set; }

    /// <summary>Round trip of the ping the offset came from.</summary>
    public TimeSpan ClockRtt { get; private set; }

    /// <summary>Progress (0…1) of the take being received, or null.</summary>
    public double? SyncProgress { get; private set; }

    public event Action<RemoteDevice>? Changed;
    public event Action<RemoteDevice, RemoteTakeResult>? TakeReceived;

    internal RemoteDevice(RemoteNetwork net, string id, string name, string platform, IEnumerable<RemoteEndpoint> endpoints)
    {
        _net = net;
        Id = id;
        Name = name;
        Platform = platform;
        _endpoints = Order(endpoints);
        _run = Task.Run(RunAsync);
    }

    private List<RemoteEndpoint> Order(IEnumerable<RemoteEndpoint> eps) =>
        eps.OrderBy(e => _net.Transports.FirstOrDefault(t => t.Name == e.Transport)?.Preference ?? 99).ToList();

    internal void AddEndpoints(IEnumerable<RemoteEndpoint> eps)
    {
        lock (_lock)
        {
            _endpoints = Order(_endpoints.Concat(eps).DistinctBy(e => (e.Transport, e.Address)));
        }
    }

    private void SetState(RemoteDeviceState s, string? error = null)
    {
        State = s;
        Error = error;
        Changed?.Invoke(this);
    }

    // ------------------------------------------------------------------ connection

    private async Task RunAsync()
    {
        var ct = _life.Token;
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            string? lastError = null;
            List<RemoteEndpoint> eps;
            lock (_lock) { eps = [.. _endpoints]; }
            foreach (var ep in eps)
            {
                try
                {
                    await ConnectOnceAsync(ep, ct).ConfigureAwait(false);
                    lastError = null;
                    break;
                }
                catch (RejectedException ex)
                {
                    lock (_lock)
                    {
                        State = RemoteDeviceState.Rejected;
                        Error = ex.Message;
                        foreach (var pending in _takes.Values) { pending.TrySetException(new InvalidOperationException(ex.Message)); }
                        foreach (var pending in _starting.Values) { pending.TrySetException(new InvalidOperationException(ex.Message)); }
                        foreach (var pending in _pausing.Values) { pending.TrySetException(new InvalidOperationException(ex.Message)); }
                    }
                    Changed?.Invoke(this);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    lastError = ex.Message;
                }
                catch (Exception)
                {
                    return;
                }
            }
            if (_link is { } link && !link.IsClosed)
            {
                delay = TimeSpan.FromSeconds(2);
                await link.Completion.WaitAsync(ct).ContinueWith(_ => { }).ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                {
                    return;
                }
            }
            SetState(RemoteDeviceState.Reconnecting, lastError ?? "Connection lost. Reconnecting…");
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 1.5));
        }
    }

    private sealed class RejectedException(string message) : Exception(message);

    private async Task ConnectOnceAsync(RemoteEndpoint ep, CancellationToken ct)
    {
        var transport = _net.Transports.FirstOrDefault(t => t.Name == ep.Transport)
            ?? throw new InvalidOperationException($"No {ep.Transport} support.");
        var stream = await transport.ConnectAsync(ep, ct).ConfigureAwait(false);
        var link = new RemoteLink(stream, transport.Name);
        lock (_lock)
        {
            if (ct.IsCancellationRequested)
            {
                link.Close();
                ct.ThrowIfCancellationRequested();
            }
            _link = link;
        }
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? rejectReason = null;
        link.Received += (l, kind, payload) =>
        {
            if (kind == RemoteMsg.Reject)
            {
                rejectReason = RemoteLink.Parse<RejectMsg>(payload).Reason;
                accepted.TrySetResult(false);
            }
            else if (kind == RemoteMsg.Accept)
            {
                accepted.TrySetResult(true);
            }
            else
            {
                OnMessage(l, kind, payload);
            }
        };
        link.Closed += (l, e) =>
        {
            accepted.TrySetException(e ?? new IOException("Closed."));
            AbortIncoming();
            lock (_lock)
            {
                foreach (var s in _starting.Values) { s.TrySetException(new IOException($"{Name} disconnected.")); }
                _starting.Clear();
                foreach (var s in _pausing.Values) { s.TrySetException(new IOException($"{Name} disconnected.")); }
                _pausing.Clear();
            }
        };
        try
        {
            var hello = await RemoteLink.ExchangeHelloAsync(link, _net.Hello(wantsControl: true), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (hello.Id != Id)
            {
                throw new InvalidOperationException("A different app answered at this address.");
            }
            Name = hello.Name;
            Platform = hello.Platform;
            HasVideo = hello.HasVideo;
            Protocol = hello.EffectiveLevel;
            SetState(RemoteDeviceState.AwaitingApproval);
            if (!await accepted.Task.WaitAsync(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false))
            {
                throw new RejectedException(rejectReason ?? $"{Name} did not allow remote control.");
            }
        }
        catch
        {
            link.Close();
            throw;
        }
        lock (_lock) { _samples.Clear(); }
        ct.ThrowIfCancellationRequested();
        SetState(RemoteDeviceState.Connected);
        RequestDownloads();
        _ping = PingLoopAsync(link);
    }

    private async Task PingLoopAsync(RemoteLink link)
    {
        // A quick burst for a first estimate, then keep refreshing (clocks drift slowly; links change).
        for (var i = 0; !link.IsClosed; i++)
        {
            try
            {
                await link.SendAsync(RemoteMsg.Ping, new PingMsg(MediaClock.Now)).ConfigureAwait(false);
                await Task.Delay(i < 12 ? 120 : 1000, _life.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    // ------------------------------------------------------------------ messages

    private void OnMessage(RemoteLink link, RemoteMsg kind, ReadOnlyMemory<byte> payload)
    {
        try
        {
            switch (kind)
            {
                case RemoteMsg.Pong:
                    OnPong(RemoteLink.Parse<PongMsg>(payload));
                    break;
                case RemoteMsg.Status:
                    var st = RemoteLink.Parse<StatusMsg>(payload);
                    Status = st;
                    // A take still running from before a lost connection that the controller already ended: stop it.
                    if (st.Recording && st.TakeId is { } running && _net.ActiveTakeId != running)
                    {
                        _ = link.SendAsync(RemoteMsg.Stop, new StopMsg(running));
                    }
                    Changed?.Invoke(this);
                    break;
                case RemoteMsg.Started:
                    var started = RemoteLink.Parse<StartedMsg>(payload);
                    lock (_lock)
                    {
                        if (_starting.Remove(started.TakeId, out var tcs)) { tcs.TrySetResult(started); }
                    }
                    break;
                case RemoteMsg.Paused:
                    var paused = RemoteLink.Parse<PausedMsg>(payload);
                    lock (_lock)
                    {
                        if (_pausing.Remove((paused.TakeId, paused.Paused), out var ptcs)) { ptcs.TrySetResult(paused); }
                    }
                    break;
                case RemoteMsg.RecordRequest:
                    RecordRequested?.Invoke(this);
                    _net.RaiseRecordRequested(this);
                    break;
                case RemoteMsg.TakeReady:
                    OnTakeReady(link, RemoteLink.Parse<TakeReadyMsg>(payload));
                    break;
                case RemoteMsg.FileBegin:
                    _incoming?.Begin(RemoteLink.Parse<FileBeginMsg>(payload));
                    break;
                case RemoteMsg.FileData:
                    _incoming?.Data(payload.Span);
                    SyncProgress = _incoming?.Progress;
                    break;
                case RemoteMsg.FileEnd:
                    if (_incoming is { } inc && inc.End(RemoteLink.Parse<FileEndMsg>(payload)))
                    {
                        FinishIncoming(link, inc);
                    }
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            AbortIncoming();
            Error = ex.Message;
            link.Close(ex);
        }
    }

    private void OnPong(PongMsg p)
    {
        var t3 = MediaClock.Now;
        var rtt = t3 - p.T1;
        if (rtt < 0)
        {
            return;
        }
        var offset = p.T2 - (p.T1 + t3) / 2;
        lock (_lock)
        {
            _samples.Add((rtt, offset));
            // Keep ~2 minutes: during a file sync pings wait behind file data, and only the old, fast samples are accurate.
            if (_samples.Count > 120)
            {
                _samples.RemoveAt(0);
            }
            var best = _samples.MinBy(s => s.Rtt);
            ClockOffset = best.Offset;
            ClockRtt = TimeSpan.FromTicks(best.Rtt);
        }
    }

    // ------------------------------------------------------------------ takes

    /// <summary>
    /// Asks the remote to start take <paramref name="takeId"/> at <paramref name="startAt"/> (controller clock) and waits for it to begin.
    /// Returns the remote's actual start relative to <paramref name="startAt"/> in ms.
    /// False <paramref name="downloadAfterRecording"/> requires level 3 and keeps files pending until <see cref="DownloadAsync"/>.
    /// </summary>
    public async Task<double> StartAsync(string takeId, long startAt, bool video, bool downloadAfterRecording = true)
    {
        var link = _link;
        if (State != RemoteDeviceState.Connected || link is null || link.IsClosed)
        {
            throw new InvalidOperationException($"{Name} is not connected.");
        }
        if (!downloadAfterRecording && !SupportsDeferredDownloads)
        {
            throw new NotSupportedException($"{Name} doesn't support deferred downloads.");
        }
        var offset = ClockOffset ?? throw new InvalidOperationException($"{Name}: clock not synchronised yet.");
        var takeFolder = _net.FolderFor(takeId) ?? throw new InvalidOperationException("Register the take before starting it.");
        var sub = _net.DeviceFolder(takeId, Id, Name);
        if (!TakeManifest.UpdateExisting(takeFolder, manifest =>
        {
            var device = manifest.Device(Id, Name);
            device.Folder = sub;
            device.DownloadRequested = downloadAfterRecording;
            device.Status = "recording";
        })) { throw new InvalidOperationException("The take was deleted."); }
        var tcs = new TaskCompletionSource<StartedMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _starting[takeId] = tcs;
            _takes[takeId] = new TaskCompletionSource<RemoteTakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        StartedMsg started;
        try
        {
            await link.SendAsync(RemoteMsg.Start, new StartMsg(takeId, startAt + offset, video, downloadAfterRecording)).ConfigureAwait(false);
            var wait = TimeSpan.FromTicks(Math.Max(0, startAt - MediaClock.Now)) + TimeSpan.FromSeconds(6);
            try
            {
                started = await tcs.Task.WaitAsync(wait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException($"{Name} did not start in time.");
            }
            if (started.Error is { } error)
            {
                throw new InvalidOperationException($"{Name}: {error}");
            }
        }
        catch
        {
            lock (_lock)
            {
                if (_takes.Remove(takeId, out var pending)) { pending.TrySetCanceled(); }
            }
            TakeManifest.UpdateExisting(takeFolder, manifest => manifest.Devices.RemoveAll(device => device.Id == Id));
            throw;
        }
        finally
        {
            lock (_lock)
            {
                if (_starting.TryGetValue(takeId, out var pending) && ReferenceEquals(pending, tcs)) { _starting.Remove(takeId); }
            }
        }
        var ms = (started.T0 - offset - startAt) / 10_000.0;
        lock (_lock) { _startOffsets[takeId] = (ms, ClockRtt.TotalMilliseconds / 2); }
        TakeManifest.UpdateExisting(takeFolder, manifest =>
        {
            var device = manifest.Device(Id, Name);
            device.StartOffsetMs = ms;
            device.ClockUncertaintyMs = ClockRtt.TotalMilliseconds / 2;
        });
        return ms;
    }

    /// <summary>
    /// Pauses the remote's take at <paramref name="pauseAt"/> (controller clock; e.g. now + 0.3 s so every device pauses together)
    /// and waits for its reply. Returns the remote's actual instant relative to <paramref name="pauseAt"/> in ms.
    /// Records the pause in <c>take.json</c>. Throws <see cref="NotSupportedException"/> for a remote with <see cref="SupportsPause"/> false.
    /// </summary>
    public Task<double> PauseAsync(string takeId, long pauseAt) => ChangePauseAsync(takeId, pauseAt, pause: true);

    /// <summary>Resumes the remote's take at <paramref name="resumeAt"/> (controller clock); see <see cref="PauseAsync"/>.</summary>
    public Task<double> ResumeAsync(string takeId, long resumeAt) => ChangePauseAsync(takeId, resumeAt, pause: false);

    private async Task<double> ChangePauseAsync(string takeId, long at, bool pause)
    {
        if (!SupportsPause)
        {
            throw new NotSupportedException($"{Name} doesn't support pause.");
        }
        var link = _link;
        if (State != RemoteDeviceState.Connected || link is null || link.IsClosed)
        {
            throw new InvalidOperationException($"{Name} is not connected.");
        }
        var offset = ClockOffset ?? throw new InvalidOperationException($"{Name}: clock not synchronised yet.");
        var tcs = new TaskCompletionSource<PausedMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool added;
        lock (_lock) { added = _pausing.TryAdd((takeId, pause), tcs); }
        if (!added)
        {
            throw new InvalidOperationException($"{Name}: a {(pause ? "pause" : "resume")} is already pending.");
        }
        PausedMsg reply;
        try
        {
            if (pause)
            {
                await link.SendAsync(RemoteMsg.Pause, new PauseMsg(takeId, at + offset)).ConfigureAwait(false);
            }
            else
            {
                await link.SendAsync(RemoteMsg.Resume, new ResumeMsg(takeId, at + offset)).ConfigureAwait(false);
            }
            var wait = TimeSpan.FromTicks(Math.Max(0, at - MediaClock.Now)) + TimeSpan.FromSeconds(6);
            reply = await tcs.Task.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"{Name} did not answer in time.");
        }
        finally
        {
            lock (_lock)
            {
                if (_pausing.TryGetValue((takeId, pause), out var cur) && ReferenceEquals(cur, tcs)) { _pausing.Remove((takeId, pause)); }
            }
        }
        if (reply.Error is { } e)
        {
            throw new InvalidOperationException($"{Name}: {e}");
        }
        if (_net.FolderFor(takeId) is { } folder && _net.ScheduledStart(takeId) is { } startAt)
        {
            var ms = (at - startAt) / 10_000.0;
            TakeManifest.UpdateExisting(folder, m =>
            {
                var d = m.Device(Id, Name);
                if (pause)
                {
                    d.Pauses.Add(new ManifestPause { StartMs = ms });
                }
                else if (d.Pauses.LastOrDefault() is { EndMs: null } open)
                {
                    open.EndMs = ms;
                }
            });
        }
        return (reply.At - offset - at) / 10_000.0;
    }

    public Task StopAsync(string takeId)
    {
        if (_net.FolderFor(takeId) is { } folder)
        {
            TakeManifest.UpdateExisting(folder, manifest =>
            {
                var device = manifest.Devices.FirstOrDefault(device => device.Id == Id);
                if (device is { Status: "recording" }) { device.Status = device.DownloadRequested ? "syncing" : "pending"; }
            });
        }
        return _link is { IsClosed: false } link ? link.SendAsync(RemoteMsg.Stop, new StopMsg(takeId)) : Task.CompletedTask;
    }

    /// <summary>Persists download intent and completes when the take arrives. Connecting/reconnecting requests are sent after acceptance.</summary>
    public Task<RemoteTakeResult> DownloadAsync(string takeId)
    {
        var folder = _net.FolderFor(takeId) ?? throw new InvalidOperationException("Unknown take.");
        var manifest = TakeManifest.Load(folder) ?? throw new InvalidOperationException("The take was deleted.");
        var device = manifest.Devices.FirstOrDefault(device => device.Id == Id && device.Role == "remote")
            ?? throw new InvalidOperationException("This device did not record the take.");
        if (device.Status == "synced" && device.Files.All(file => File.Exists(Path.Combine(folder, file))))
        {
            return Task.FromResult(StoredResult(takeId, folder, device));
        }
        if (State == RemoteDeviceState.Connected && !SupportsDeferredDownloads)
        {
            throw new NotSupportedException($"{Name} doesn't support deferred downloads.");
        }
        if (State is RemoteDeviceState.Disconnected or RemoteDeviceState.Rejected)
        {
            throw new InvalidOperationException($"{Name} is not connecting.");
        }
        bool retry;
        lock (_lock)
        {
            _explicitDownloads.Add(takeId);
            retry = _takes.TryGetValue(takeId, out var previous) && previous.Task.IsCompleted;
            if (retry) { _takes.Remove(takeId); }
        }
        if (!TakeManifest.UpdateExisting(folder, current =>
        {
            var entry = current.Devices.First(entry => entry.Id == Id);
            entry.DownloadRequested = true;
            if (entry.Status != "recording") { entry.Status = "syncing"; }
        })) { throw new InvalidOperationException("The take was deleted."); }
        var completion = WaitForTakeAsync(takeId);
        RequestDownloads(retry ? takeId : null);
        return completion;
    }

    private RemoteTakeResult StoredResult(string takeId, string folder, ManifestDevice device) =>
        new(takeId, Id, device.Name, Path.Combine(folder, device.Folder),
            device.Files.Select(file => Path.Combine(folder, file)).ToList(), device.Seconds,
            device.StartOffsetMs, device.ClockUncertaintyMs, device.Errors);

    internal void RequestDownloads(string? retryTakeId = null)
    {
        if (State != RemoteDeviceState.Connected) { return; }
        if (!SupportsDeferredDownloads)
        {
            lock (_lock)
            {
                foreach (var takeId in _explicitDownloads)
                {
                    if (_takes.TryGetValue(takeId, out var pending))
                    {
                        pending.TrySetException(new NotSupportedException($"{Name} doesn't support deferred downloads."));
                    }
                }
            }
            return;
        }
        if (_link is not { IsClosed: false } link) { return; }
        _ = SendDownloadRequestsAsync(link, retryTakeId);
    }

    private async Task SendDownloadRequestsAsync(RemoteLink link, string? retryTakeId)
    {
        try
        {
            foreach (var takeId in _net.RequestedTakes(Id))
            {
                await link.SendAsync(RemoteMsg.Download, new DownloadMsg(takeId, Retry: takeId == retryTakeId)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            link.Close(ex);
        }
    }

    /// <summary>Completes when every file of the take has arrived (possibly after a reconnect).</summary>
    public Task<RemoteTakeResult> WaitForTakeAsync(string takeId)
    {
        lock (_lock)
        {
            if (!_takes.TryGetValue(takeId, out var tcs))
            {
                if (State is RemoteDeviceState.Rejected or RemoteDeviceState.Disconnected || _life.IsCancellationRequested)
                {
                    return Task.FromException<RemoteTakeResult>(new InvalidOperationException(Error ?? $"{Name} is not connecting."));
                }
                if (_net.FolderFor(takeId) is { } folder && TakeManifest.Load(folder)?.Devices.FirstOrDefault(device => device.Id == Id) is { Status: "synced" } device)
                {
                    return Task.FromResult(StoredResult(takeId, folder, device));
                }
                tcs = new TaskCompletionSource<RemoteTakeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _takes[takeId] = tcs;
            }
            return tcs.Task;
        }
    }

    public Task SetModeAsync(bool video) =>
        _link is { IsClosed: false } link ? link.SendAsync(RemoteMsg.SetMode, new SetModeMsg(video)) : Task.CompletedTask;

    public Task ConnectMicsAsync() =>
        _link is { IsClosed: false } link ? link.SendAsync(RemoteMsg.ConnectMics) : Task.CompletedTask;

    private void OnTakeReady(RemoteLink link, TakeReadyMsg msg)
    {
        AbortIncoming();
        var takeFolder = _net.FolderFor(msg.TakeId);
        if (takeFolder is null || !Directory.Exists(takeFolder))
        {
            return;
        }
        var manifestDevice = TakeManifest.Load(takeFolder)?.Devices.FirstOrDefault(device => device.Id == Id && device.Role == "remote");
        if (manifestDevice is null) { return; }
        if (!msg.FilesFollow)
        {
            TakeManifest.UpdateExisting(takeFolder, manifest =>
            {
                var device = manifest.Device(Id, Name);
                device.Seconds = msg.Seconds;
                device.Errors = [.. msg.Errors];
                if (device.Status != "synced") { device.Status = device.DownloadRequested ? "syncing" : "pending"; }
            });
            Changed?.Invoke(this);
            return;
        }
        if (SupportsDeferredDownloads && !manifestDevice.DownloadRequested) { return; }
        var sub = _net.DeviceFolder(msg.TakeId, Id, Name);
        var folder = Path.Combine(takeFolder, sub);
        Directory.CreateDirectory(folder);
        TakeManifest.UpdateExisting(takeFolder, m =>
        {
            var d = m.Device(Id, Name);
            d.Folder = sub;
            d.Status = "syncing";
        });
        _incoming = new Incoming(msg, folder);
        SyncProgress = 0;
        Changed?.Invoke(this);
        if (msg.Files.Count == 0)
        {
            FinishIncoming(link, _incoming);
        }
    }

    private void FinishIncoming(RemoteLink link, Incoming inc)
    {
        _incoming = null;
        SyncProgress = null;
        var msg = inc.Take;
        double? startOffset = null;
        double? uncertainty = null;
        bool known;
        (double OffsetMs, double UncertaintyMs) atStart;
        lock (_lock) { known = _startOffsets.Remove(msg.TakeId, out atStart); }
        if (known)
        {
            // Measured when the take started, on a quiet link: better than an estimate taken during the transfer.
            startOffset = atStart.OffsetMs;
            uncertainty = atStart.UncertaintyMs;
        }
        else if (_net.FolderFor(msg.TakeId) is { } storedFolder && TakeManifest.Load(storedFolder)?.Devices.FirstOrDefault(device => device.Id == Id) is { } stored)
        {
            startOffset = stored.StartOffsetMs;
            uncertainty = stored.ClockUncertaintyMs;
        }
        var result = new RemoteTakeResult(msg.TakeId, Id, Name, inc.Folder, inc.Files, msg.Seconds, startOffset, uncertainty, msg.Errors);
        var transferFailed = msg.Files.Count == 0 && msg.Errors.Count > 0;
        if (_net.FolderFor(msg.TakeId) is { } takeFolder)
        {
            var root = takeFolder;
            if (!TakeManifest.UpdateExisting(root, m =>
            {
                var d = m.Device(Id, Name);
                d.Role = "remote";
                d.Folder = Path.GetRelativePath(root, inc.Folder);
                d.Files = inc.Files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).ToList();
                d.Seconds = msg.Seconds;
                d.StartOffsetMs = startOffset ?? d.StartOffsetMs;
                d.ClockUncertaintyMs = uncertainty ?? d.ClockUncertaintyMs;
                d.Errors = [.. msg.Errors];
                d.Status = transferFailed ? "failed" : "synced";
            })) { return; }
        }
        else { return; }
        if (!transferFailed) { _ = link.SendAsync(RemoteMsg.TakeAck, new TakeAckMsg(msg.TakeId)); }
        TaskCompletionSource<RemoteTakeResult>? tcs;
        lock (_lock) { _takes.TryGetValue(msg.TakeId, out tcs); }
        tcs?.TrySetResult(result);
        TakeReceived?.Invoke(this, result);
        Changed?.Invoke(this);
    }

    private void AbortIncoming()
    {
        _incoming?.Abort();
        _incoming = null;
        SyncProgress = null;
    }

    /// <summary>Files of one take arriving in order: FileBegin, FileData…, FileEnd (SHA-256 checked).</summary>
    private sealed class Incoming(TakeReadyMsg take, string folder)
    {
        private readonly long _total = Math.Max(1, take.Files.Sum(f => f.Size));
        private FileStream? _fs;
        private IncrementalHash? _hash;
        private string? _path;
        private long _done;
        private long _fileBytes;
        private RemoteFile? _expected;

        public TakeReadyMsg Take => take;
        public string Folder => folder;
        public List<string> Files { get; } = [];
        public double Progress => Math.Min(1, (double)_done / _total);

        public void Begin(FileBeginMsg m)
        {
            if (_fs is not null || Files.Count >= take.Files.Count) { throw new InvalidDataException("Unexpected file."); }
            _expected = take.Files[Files.Count];
            if (m.TakeId != take.TakeId || m.Name != _expected.Name || m.Size != _expected.Size || m.Size < 0)
            {
                throw new InvalidDataException("File does not match the take manifest.");
            }
            _fileBytes = 0;
            Abort();
            // Never trust a path from the network; older remotes may send names that are invalid here (e.g. "Bluetooth: Mic.wav").
            var raw = m.Name.Replace('\\', '/');
            raw = raw[(raw.LastIndexOf('/') + 1)..];
            var name = raw.Length == 0 ? "" : FileNames.Sanitize(raw, "track");
            if (name.Length > 0)
            {
                // Two remote names can collapse to the same sanitized name: keep both files.
                var stem = Path.GetFileNameWithoutExtension(name);
                var ext = Path.GetExtension(name);
                for (var i = 2; Files.Any(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)); i++)
                {
                    name = $"{stem}_{i}{ext}";
                }
            }
            if (name.Length == 0 || m.TakeId != take.TakeId)
            {
                throw new InvalidDataException("Unexpected file.");
            }
            _path = Path.Combine(folder, name);
            _fs = new FileStream(_path + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        public void Data(ReadOnlySpan<byte> data)
        {
            if (_fs is null)
            {
                throw new InvalidDataException("File data without a file.");
            }
            if (data.Length > _expected!.Size - _fileBytes) { throw new InvalidDataException("File is larger than announced."); }
            _fs.Write(data);
            _hash!.AppendData(data);
            _done += data.Length;
            _fileBytes += data.Length;
        }

        /// <summary>Returns true when this was the last file of the take.</summary>
        public bool End(FileEndMsg m)
        {
            if (_fs is null || _path is null)
            {
                throw new InvalidDataException("File end without a file.");
            }
            if (m.TakeId != take.TakeId || m.Name != _expected!.Name || _fileBytes != _expected.Size)
            {
                throw new InvalidDataException("Incomplete or unexpected file.");
            }
            _fs.Dispose();
            _fs = null;
            var sha = Convert.ToHexString(_hash!.GetHashAndReset());
            _hash.Dispose();
            _hash = null;
            if (!string.Equals(sha, m.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(_path + ".part");
                throw new InvalidDataException($"{Path.GetFileName(_path)} arrived damaged; it will be sent again.");
            }
            File.Move(_path + ".part", _path, overwrite: true);
            Files.Add(_path);
            _path = null;
            return Files.Count == take.Files.Count;
        }

        public void Abort()
        {
            if (_fs is not null)
            {
                _fs.Dispose();
                _fs = null;
                try { File.Delete(_path + ".part"); } catch (IOException) { }
            }
            _hash?.Dispose();
            _hash = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            await _disposed.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            _life.Cancel();
            AbortIncoming();
            lock (_lock)
            {
                _link?.Close();
                foreach (var pending in _takes.Values) { pending.TrySetCanceled(); }
                foreach (var pending in _starting.Values) { pending.TrySetCanceled(); }
                foreach (var pending in _pausing.Values) { pending.TrySetCanceled(); }
                _takes.Clear();
                _starting.Clear();
                _pausing.Clear();
            }
            await _run.ConfigureAwait(false);
            if (_ping is { } ping) { await ping.ConfigureAwait(false); }
            SetState(RemoteDeviceState.Disconnected);
        }
        finally
        {
            _life.Dispose();
            _disposed.TrySetResult();
        }
    }
}
