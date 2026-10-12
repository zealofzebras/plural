namespace Plural.Core.Remote;

/// <summary>A found app, merged over every transport that reached it.</summary>
public sealed record DiscoveredPeer(string Id, string Name, string Platform, IReadOnlyList<RemoteEndpoint> Endpoints);

/// <summary>
/// Remote mode for one app: makes it findable and controllable (listening on every transport) and lets it control others.
/// An app is either controlled by one controller or controls any number of remotes, never both at once.
/// </summary>
public sealed class RemoteNetwork : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly List<RemoteDevice> _devices = [];
    private readonly Dictionary<string, (string Folder, long StartAt, Dictionary<string, string> DeviceFolders)> _takes = [];
    private CancellationTokenSource? _listen;
    private readonly Dictionary<string, string?> _listenErrors = [];

    public RemoteIdentity Identity { get; }
    public IReadOnlyList<IRemoteTransport> Transports { get; }
    public RemoteResponder Responder { get; }

    /// <summary>Whether this device has a camera (told to controllers).</summary>
    public bool HasVideo { get; set; }

    /// <summary>When false, control requests are refused (the app stays findable only while listening).</summary>
    public bool AllowControl { get; set; } = true;

    public bool IsListening => _listen is not null;

    /// <summary>Per transport: null when listening works, else why not (e.g. "Bluetooth is off").</summary>
    public IReadOnlyDictionary<string, string?> ListenErrors { get { lock (_lock) { return new Dictionary<string, string?>(_listenErrors); } } }

    public IReadOnlyList<RemoteDevice> Devices { get { lock (_lock) { return [.. _devices]; } } }

    /// <summary>Remotes this app is connected to (or trying to reach).</summary>
    public bool IsControlling => Devices.Any(d => d.State is not (RemoteDeviceState.Disconnected or RemoteDeviceState.Rejected));

    /// <summary>The controller's take in progress (remotes still running another take are told to stop it).</summary>
    public string? ActiveTakeId { get; set; }

    public event Action? DevicesChanged;

    /// <summary>
    /// A remote's mic button asked to toggle recording. The app handles it like its own Record button (and starts or stops
    /// everyone, remotes included). Raised on a background thread.
    /// </summary>
    public event Action<RemoteDevice>? RecordRequested;

    internal void RaiseRecordRequested(RemoteDevice device) => RecordRequested?.Invoke(device);

    public RemoteNetwork(RemoteIdentity identity, IEnumerable<IRemoteTransport> transports, IRemoteRecorder recorder)
    {
        Identity = identity;
        Transports = [.. transports.OrderBy(t => t.Preference)];
        Responder = new RemoteResponder(this, recorder);
    }

    internal HelloMsg Hello(bool wantsControl) =>
        new(RemoteProtocol.WireVersion, Identity.Id, Identity.Name, Identity.Platform, HasVideo, wantsControl, ProtocolVersion);

    /// <summary>Feature level announced in Hello; tests lower it to play an older app.</summary>
    internal int ProtocolVersion { get; set; } = RemoteProtocol.Version;

    // ------------------------------------------------------------------ being found / controlled

    /// <summary>Makes this app findable on every transport (each one retries quietly if its radio is off).</summary>
    public void StartListening()
    {
        if (_listen is not null)
        {
            return;
        }
        var cts = _listen = new CancellationTokenSource();
        foreach (var t in Transports)
        {
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        lock (_lock) { _listenErrors[t.Name] = null; }
                        await t.ListenAsync(Identity, s => Responder.HandleAsync(s, t.Name), cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!cts.IsCancellationRequested)
                    {
                        lock (_lock) { _listenErrors[t.Name] = ex.Message; }
                    }
                    catch (Exception)
                    {
                        return;
                    }
                    try { await Task.Delay(TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            });
        }
    }

    public void StopListening()
    {
        _listen?.Cancel();
        _listen = null;
        lock (_lock) { _listenErrors.Clear(); }
    }

    // ------------------------------------------------------------------ finding and controlling others

    /// <summary>
    /// Searches every transport at once for <paramref name="duration"/>; <paramref name="found"/> gets each app as soon as
    /// any transport sees it, again with more endpoints when another transport finds it too.
    /// </summary>
    public async Task SearchAsync(Action<DiscoveredPeer> found, TimeSpan duration, CancellationToken ct = default)
    {
        var peers = new Dictionary<string, DiscoveredPeer>();
        var gate = new object();
        void OnFound(RemoteEndpoint ep)
        {
            DiscoveredPeer p;
            lock (gate)
            {
                if (peers.TryGetValue(ep.PeerId, out var known) && known.Endpoints.Contains(ep))
                {
                    return; // the same answer again (discovery asks several times, on every network)
                }
                p = peers.TryGetValue(ep.PeerId, out var old)
                    ? old with { Name = ep.Name, Endpoints = [.. old.Endpoints.Where(e => (e.Transport, e.Address) != (ep.Transport, ep.Address)), ep] }
                    : new DiscoveredPeer(ep.PeerId, ep.Name, ep.Platform, [ep]);
                peers[ep.PeerId] = p;
            }
            found(p);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        await Task.WhenAll(Transports.Select(async t =>
        {
            try { await t.DiscoverAsync(Identity, OnFound, cts.Token).ConfigureAwait(false); }
            catch (Exception) { /* this transport is unavailable; the others still search */ }
        })).ConfigureAwait(false);
    }

    /// <summary>Starts controlling <paramref name="peer"/> (the remote asks its user first). Returns the existing device if already added.</summary>
    public RemoteDevice Connect(DiscoveredPeer peer)
    {
        if (Responder.Controller is { Connected: true } c)
        {
            throw new InvalidOperationException($"This device is controlled by {c.Name}. Disconnect it first.");
        }
        RemoteDevice? device;
        lock (_lock)
        {
            device = _devices.FirstOrDefault(d => d.Id == peer.Id);
            if (device is not null && device.State is RemoteDeviceState.Disconnected or RemoteDeviceState.Rejected)
            {
                _devices.Remove(device);
                device = null;
            }
            if (device is null)
            {
                device = new RemoteDevice(this, peer.Id, peer.Name, peer.Platform, peer.Endpoints);
                _devices.Add(device);
            }
            else
            {
                device.AddEndpoints(peer.Endpoints);
            }
        }
        DevicesChanged?.Invoke();
        return device;
    }

    public async Task DisconnectAsync(RemoteDevice device)
    {
        lock (_lock) { _devices.Remove(device); }
        DevicesChanged?.Invoke();
        await device.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ take bookkeeping (controller)

    /// <summary>Remembers where take <paramref name="takeId"/> lives and its scheduled start, so late files land in the right place.</summary>
    public void RegisterTake(string takeId, string folder, long startAt)
    {
        lock (_lock) { _takes[takeId] = (folder, startAt, []); }
    }

    internal string? FolderFor(string takeId)
    {
        lock (_lock) { return _takes.TryGetValue(takeId, out var t) ? t.Folder : null; }
    }

    internal long? ScheduledStart(string takeId)
    {
        lock (_lock) { return _takes.TryGetValue(takeId, out var t) ? t.StartAt : null; }
    }

    /// <summary>A sub-folder name for a device in a take, unique even when two devices share a name.</summary>
    internal string DeviceFolder(string takeId, string deviceId, string deviceName)
    {
        lock (_lock)
        {
            if (!_takes.TryGetValue(takeId, out var t))
            {
                return Sanitize(deviceName);
            }
            if (t.DeviceFolders.TryGetValue(deviceId, out var existing))
            {
                return existing;
            }
            var name = Sanitize(deviceName);
            var unique = name;
            for (var i = 2; t.DeviceFolders.ContainsValue(unique); i++) { unique = $"{name} ({i})"; }
            t.DeviceFolders[deviceId] = unique;
            return unique;
        }
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Device" : clean;
    }

    public async ValueTask DisposeAsync()
    {
        StopListening();
        Responder.Disconnect();
        foreach (var d in Devices)
        {
            await d.DisposeAsync().ConfigureAwait(false);
        }
        lock (_lock) { _devices.Clear(); }
    }
}
