using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.App.Services;
using Plural.Core.Platform;
using Plural.Core.Recording;
using Plural.Core.Remote;
using Plural.Core.Session;

namespace Plural.App.ViewModels;

// Remote mode: this app controls other apps (search, connect, record together, collect files) or is controlled by one.
// Transports (Wi-Fi, Bluetooth) are never shown; a device is just a name. Spec: Docs/Plural/Remote.md.
public partial class MainViewModel
{
    private RemoteNetwork? _remote;
    private string _deviceId = "";
    private List<string> _trustedControllers = [];
    private volatile StatusMsg _remoteStatus = new([], false, null, 0, false, false, false, null);
    private TaskCompletionSource<bool>? _approval;
    private string? _approvalId;
    private ControllerTake? _ctrlTake;
    private readonly List<(RemoteDevice Device, string Folder)> _syncing = [];
    private CancellationTokenSource? _searchCts;
    private ControllerTake? _downloadChoiceTake;

    public ObservableCollection<RemoteDownloadChoice> DownloadChoices { get; } = [];
    [ObservableProperty] public partial bool IsDownloadChoiceOpen { get; set; }
    [ObservableProperty] public partial bool DownloadAfterRecording { get; set; }
    [ObservableProperty] public partial string DownloadCompatibility { get; set; } = "";

    partial void OnDownloadAfterRecordingChanged(bool value) => SaveSettings();

    internal void RestoreRemoteTakes()
    {
        try { _remote?.RestoreTakes(RecordingRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Status = "Cannot restore remote takes: " + ex.Message;
        }
    }

    /// <summary>A take this app started on itself and/or remotes.</summary>
    private sealed record ControllerTake(string TakeId, string Folder, string Stamp, long StartAt, bool Local, List<RemoteDevice> Remotes);

    public ObservableCollection<RemoteDeviceViewModel> Remotes { get; } = [];
    public ObservableCollection<PeerViewModel> FoundPeers { get; } = [];

    [ObservableProperty] public partial bool HasRemotes { get; set; }
    [ObservableProperty] public partial bool HasConnectedRemote { get; set; }
    [ObservableProperty] public partial bool IsDevicesOpen { get; set; }
    [ObservableProperty] public partial bool IsSearching { get; set; }
    [ObservableProperty] public partial bool HasFoundPeers { get; set; }
    [ObservableProperty] public partial string SearchStatus { get; set; } = "";
    [ObservableProperty] public partial bool AllowRemoteControl { get; set; } = true;
    [ObservableProperty] public partial string FindableText { get; set; } = "";
    [ObservableProperty] public partial bool IsControlled { get; set; }
    [ObservableProperty] public partial bool IsControllerConnected { get; set; }
    [ObservableProperty] public partial string ControllerText { get; set; } = "";
    [ObservableProperty] public partial string ControllerDetail { get; set; } = "";
    [ObservableProperty] public partial bool IsApprovalOpen { get; set; }
    [ObservableProperty] public partial string ApprovalText { get; set; } = "";
    [ObservableProperty] public partial bool ApprovalRemember { get; set; }
    [ObservableProperty] public partial bool ShowEmptyState { get; set; } = true;

    public string ThisDeviceName => _remote?.Identity.Name ?? "";
    public bool CanUseRemote => _remote is not null;

    partial void OnHasRemotesChanged(bool value) => UpdateEmptyState();
    partial void OnHasMikesChanged(bool value) => UpdateEmptyState();
    partial void OnHasConnectedRemoteChanged(bool value) => RefreshRecordState();
    partial void OnIsControllerConnectedChanged(bool value) => RefreshRecordState();

    private void UpdateEmptyState() => ShowEmptyState = !HasMikes && !HasRemotes;

    /// <summary>Creates the remote network (called from the constructor, after settings are loaded).</summary>
    private void CreateRemote(IAppHost host, MikeHub hub)
    {
        if (string.IsNullOrEmpty(_deviceId))
        {
            _deviceId = Guid.NewGuid().ToString("N");
            SaveSettings();
        }
        try
        {
            var identity = new RemoteIdentity(_deviceId, host.DeviceName, host.PlatformName);
            _remote = new RemoteNetwork(identity, host.CreateRemoteTransports(hub), new RemoteRecorder(this))
            {
                HasVideo = HasVideo,
                AllowControl = AllowRemoteControl,
            };
            _remote.DevicesChanged += () => Dispatcher.UIThread.Post(SyncRemoteList);
            RestoreRemoteTakes();
        }
        catch (Exception ex)
        {
            Status = "Remote mode is unavailable: " + ex.Message;
        }
    }

    /// <summary>Makes this device findable (once the UI is up: Android asks for Bluetooth permission first).</summary>
    private void StartRemoteListening()
    {
        if (_remote is not null && AllowRemoteControl)
        {
            _remote.StartListening();
        }
    }

    partial void OnAllowRemoteControlChanged(bool value)
    {
        if (_remote is null || _loading)
        {
            return;
        }
        _remote.AllowControl = value;
        if (value)
        {
            _remote.StartListening();
        }
        else
        {
            _remote.StopListening();
        }
        SaveSettings();
    }

    // ---------------------------------------------------------------- finding and controlling others

    [RelayCommand]
    private async Task OpenDevicesAsync()
    {
        IsDevicesOpen = true;
        if (FoundPeers.Count == 0)
        {
            await SearchAsync();
        }
    }

    [RelayCommand]
    private void CloseDevices()
    {
        IsDevicesOpen = false;
        _searchCts?.Cancel();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_remote is null || IsSearching || _host is null)
        {
            return;
        }
        // Bluetooth discovery needs the Bluetooth permission on Android; Wi-Fi works without it.
        try { await _host.EnsurePermissionsAsync(); } catch (Exception) { /* search Wi-Fi only */ }
        IsSearching = true;
        SearchStatus = "Searching…";
        foreach (var p in FoundPeers.ToList().Where(p => !p.IsAdded))
        {
            FoundPeers.Remove(p);
        }
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await _remote.SearchAsync(peer => Dispatcher.UIThread.Post(() => OnPeerFound(peer)), TimeSpan.FromSeconds(12), cts.Token);
        }
        catch (Exception ex)
        {
            SearchStatus = ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
        HasFoundPeers = FoundPeers.Count > 0;
        SearchStatus = FoundPeers.Count > 0
            ? ""
            : _remote.Transports.Any(t => t is BluetoothRemoteTransport)
                ? "No other devices found. Open Plural on the other device, on the same Wi-Fi or paired over Bluetooth, and search again."
                : "No other devices found. Open Plural on the other device, on the same Wi-Fi, and search again.";
    }

    private void OnPeerFound(DiscoveredPeer peer)
    {
        var existing = FoundPeers.FirstOrDefault(p => p.Peer.Id == peer.Id);
        if (existing is null)
        {
            FoundPeers.Add(new PeerViewModel(peer, this) { IsAdded = Remotes.Any(r => r.Device.Id == peer.Id) });
        }
        else
        {
            existing.Update(peer);
        }
        HasFoundPeers = true;
        SearchStatus = "";
    }

    public void ConnectPeer(PeerViewModel peer)
    {
        if (_remote is null)
        {
            return;
        }
        try
        {
            _remote.Connect(peer.Peer);
            peer.IsAdded = true;
            SyncRemoteList();
        }
        catch (Exception ex)
        {
            SearchStatus = ex.Message;
        }
    }

    public async Task DisconnectRemoteAsync(RemoteDeviceViewModel vm)
    {
        if (_remote is null || (IsRecordingUi && _ctrlTake?.Remotes.Contains(vm.Device) == true))
        {
            return;
        }
        await _remote.DisconnectAsync(vm.Device);
        foreach (var p in FoundPeers.Where(p => p.Peer.Id == vm.Device.Id))
        {
            p.IsAdded = false;
        }
        SyncRemoteList();
    }

    private void SyncRemoteList()
    {
        if (_remote is null)
        {
            return;
        }
        var devices = _remote.Devices;
        foreach (var vm in Remotes.Where(r => !devices.Contains(r.Device)).ToList())
        {
            Remotes.Remove(vm);
        }
        foreach (var d in devices.Where(d => Remotes.All(r => r.Device != d)))
        {
            Remotes.Add(new RemoteDeviceViewModel(d, this));
            d.TakeReceived += OnRemoteTakeReceived;
        }
        HasRemotes = Remotes.Count > 0;
    }

    private void OnRemoteTakeReceived(RemoteDevice device, RemoteTakeResult result) =>
        Dispatcher.UIThread.Post(() => _ = LoadTakesAsync());

    public RemoteDevice? FindRemote(string id) => _remote?.Devices.FirstOrDefault(device => device.Id == id);

    public async Task RequestRemoteDownloadAsync(RemoteDownloadViewModel item)
    {
        if (_remote is null) { throw new InvalidOperationException("Remote mode is unavailable."); }
        var device = FindRemote(item.DeviceId);
        if (device is null || device.State != RemoteDeviceState.Connected)
        {
            if (_host is not null)
            {
                try { await _host.EnsurePermissionsAsync(); } catch (Exception) { }
            }
            DiscoveredPeer? target = null;
            await _remote.SearchAsync(peer =>
            {
                if (peer.Id == item.DeviceId) { target = peer; }
            }, TimeSpan.FromSeconds(12));
            if (target is null)
            {
                throw new InvalidOperationException("Device not found. Open Plural on that device and retry.");
            }
            device = _remote.Connect(target);
            SyncRemoteList();
        }
        var download = device.DownloadAsync(item.TakeId);
        _ = CollectAsync(device, item.Folder, download);
        await LoadTakesAsync();
    }

    /// <summary>Called from the UI timer.</summary>
    private void RefreshRemote()
    {
        if (_remote is null)
        {
            return;
        }
        foreach (var r in Remotes)
        {
            r.Refresh(IsRecordingUi);
        }
        HasConnectedRemote = Remotes.Any(r => r.IsLive);

        // What a controller sees of this device.
        _remoteStatus = new StatusMsg(
            [.. Mikes.Select(m => new RemoteMicStatus(m.Name, m.IsStreaming, m.IsStreaming ? m.Channel.Source.PeakLevel : 0f))],
            IsRecording, _remote.Responder.ActiveTakeId, RecordedElapsed().TotalSeconds,
            HasVideo, IsVideoMode, IsCameraReady, FormatShort, IsPaused);

        var errors = _remote.ListenErrors.Where(e => e.Value is not null).Select(e => e.Value!).ToList();
        FindableText = !AllowRemoteControl ? "Other devices cannot find this one."
            : !_remote.IsListening ? "Starting…"
            : errors.Count == _remote.Transports.Count ? "Not findable: " + string.Join(" ", errors)
            : $"Other devices can find “{ThisDeviceName}” and ask to control it.";

        if (IsControlled && _remote.Responder.SendProgress is { } p)
        {
            ControllerDetail = $"Sending files… {p:P0}";
        }
        else if (IsControlled)
        {
            ControllerDetail = IsControllerConnected
                ? (IsRecording ? "Recording for the controller" : "The controller starts and stops recording")
                : (_remote.Responder.PendingTakes > 0 ? "Files saved here, waiting for the controller" : "Waiting for it to come back");
        }

        Takes.RefreshRemoteDownloads();
    }

    // ---------------------------------------------------------------- recording with remotes (controller)

    private List<RemoteDevice> ConnectedRemotes() =>
        _remote?.Devices.Where(d => d.State == RemoteDeviceState.Connected && d.ClockOffset is not null).ToList() ?? [];

    /// <summary>
    /// Starts a take on this device (if a mic is live) and every connected remote, all at one scheduled instant: the remotes get it
    /// converted to their own clocks, so the files line up. Files from remotes arrive after Stop.
    /// </summary>
    private async Task StartWithRemotesAsync(MikeHub hub, List<RemoteDevice> remotes)
    {
        var (folder, stamp) = MikeHub.NewTakeFolder(RecordingRoot);
        var takeId = $"{stamp}-{Guid.NewGuid().ToString("N")[..8]}";
        // Lead time: the slowest link must deliver Start, and a remote switching to video needs its camera.
        var lead = remotes.Any(r => r.Transport == "Bluetooth") ? 1200 : 700;
        var startAt = MediaClock.Now + TimeSpan.FromMilliseconds(lead).Ticks;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            ShowToast("Cannot start recording", ex.Message, true, null);
            return;
        }
        _remote!.RegisterTake(takeId, folder, startAt);
        _remote.ActiveTakeId = takeId;
        IsStopping = true; // blocks the button until everyone has answered
        var starts = remotes.Select(r => (Device: r, Task: r.StartAsync(takeId, startAt, r.Status?.VideoMode == true,
            downloadAfterRecording: !r.SupportsDeferredDownloads))).ToList();

        var wait = TimeSpan.FromTicks(startAt - MediaClock.Now);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
        var errors = new List<string>();
        var local = false;
        double? localOffset = null;
        if (hub.HasStreamingMic)
        {
            try
            {
                hub.StartRecordingAll(folder, stamp);
                local = true;
                localOffset = (hub.RecordingStartTime - startAt) / 10_000.0;
            }
            catch (Exception ex)
            {
                errors.Add($"{ThisDeviceName}: {ex.Message}");
            }
        }
        var started = new List<RemoteDevice>();
        var offsets = new Dictionary<string, double>();
        foreach (var (device, task) in starts)
        {
            try
            {
                offsets[device.Id] = await task;
                started.Add(device);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        IsStopping = false;
        if (!local && started.Count == 0)
        {
            _remote.ActiveTakeId = null;
            TryDeleteEmpty(folder);
            ShowToast("Cannot start recording", string.Join("\n", errors), true, null);
            return;
        }

        _ctrlTake = new ControllerTake(takeId, folder, stamp, startAt, local, started);
        var format = hub.RecordingSettings.Format.ShortName();
        TakeManifest.Update(folder, m =>
        {
            m.TakeId = takeId;
            m.Controller = ThisDeviceName;
            m.Format = format;
            if (local)
            {
                var d = m.Device(_deviceId, ThisDeviceName);
                d.Role = "controller";
                d.StartOffsetMs = localOffset;
                d.ClockUncertaintyMs = 0;
            }
            foreach (var r in started)
            {
                var d = m.Device(r.Id, r.Name);
                d.StartOffsetMs = offsets[r.Id];
                d.ClockUncertaintyMs = r.ClockRtt.TotalMilliseconds / 2;
            }
        });
        _recMics = local ? hub.Channels.Count(c => c.RecordingPath is not null) : 0;
        EnterRecordingUi();
        if (errors.Count > 0)
        {
            ShowToast("Some devices are not recording", string.Join("\n", errors), true, null);
        }
    }

    private async Task StopWithRemotesAsync(MikeHub hub, ControllerTake take)
    {
        IsStopping = true;
        _remote!.ActiveTakeId = null;
        foreach (var r in take.Remotes)
        {
            if (!r.SupportsDeferredDownloads)
            {
                _ = CollectAsync(r, take.Folder, r.WaitForTakeAsync(take.TakeId));
            }
            try { await r.StopAsync(take.TakeId); } catch (Exception) { /* lost: stopped by the status check after it reconnects */ }
        }
        RecordedTake? local = null;
        string? error = null;
        if (take.Local)
        {
            try { local = await hub.StopRecordingAllAsync(); }
            catch (Exception ex) { error = ex.Message; }
        }
        var elapsed = _recordClock.Elapsed;
        _recordClock.Reset();
        _ctrlTake = null;
        IsRecording = false;
        IsStopping = false;
        if (local is not null)
        {
            TakeManifest.Update(take.Folder, m =>
            {
                var d = m.Device(_deviceId, ThisDeviceName);
                d.Role = "controller";
                d.Files = [.. local.Files.Select(f => Path.GetRelativePath(take.Folder, f).Replace('\\', '/'))];
                d.Seconds = local.Duration.TotalSeconds;
                d.Errors = [.. local.Errors];
                d.Status = "synced";
            });
        }
        if (error is not null)
        {
            ShowToast("Recording failed on this device", error, true, null);
        }
        RequestCameraSync();
        await LoadTakesAsync();
        DownloadChoices.Clear();
        foreach (var device in take.Remotes.Where(device => device.SupportsDeferredDownloads))
        {
            DownloadChoices.Add(new RemoteDownloadChoice(device.Name, device.Id) { IsSelected = DownloadAfterRecording });
        }
        DownloadCompatibility = string.Join(", ", take.Remotes.Where(device => !device.SupportsDeferredDownloads).Select(device => device.Name));
        if (DownloadCompatibility.Length > 0)
        {
            DownloadCompatibility += ": older Plural version; files transfer automatically and cannot be postponed.";
        }
        _downloadChoiceTake = take;
        IsDownloadChoiceOpen = DownloadChoices.Count > 0;
        if (!IsDownloadChoiceOpen && DownloadCompatibility.Length > 0)
        {
            ShowToast("Automatic download", DownloadCompatibility, false, Takes.Find(take.Folder));
        }
        if (_stopReason is { } why)
        {
            _stopReason = null;
            ShowToast("Recording stopped", why, true, null);
        }
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        var take = _downloadChoiceTake;
        IsDownloadChoiceOpen = false;
        _downloadChoiceTake = null;
        if (take is null) { return; }
        foreach (var choice in DownloadChoices.Where(choice => choice.IsSelected))
        {
            var device = take.Remotes.First(device => device.Id == choice.DeviceId);
            try { _ = CollectAsync(device, take.Folder, device.DownloadAsync(take.TakeId)); }
            catch (Exception ex) { ShowToast("Download not started", $"{device.Name}: {ex.Message}", true, null); }
        }
        await LoadTakesAsync();
    }

    [RelayCommand]
    private void PostponeDownloads()
    {
        IsDownloadChoiceOpen = false;
        _downloadChoiceTake = null;
    }

    private async Task CollectAsync(RemoteDevice device, string folder, Task<RemoteTakeResult> download)
    {
        if (_syncing.Any(entry => entry.Device == device && entry.Folder == folder)) { return; }
        _syncing.Add((device, folder));
        string? error = null;
        try
        {
            var result = await download;
            if (result.Errors.Count > 0)
            {
                error = $"{device.Name}: {string.Join("; ", result.Errors)}";
            }
        }
        catch (OperationCanceledException)
        {
            error = $"{device.Name} was disconnected before its files arrived; they stay on that device.";
        }
        catch (Exception ex)
        {
            error = $"{device.Name}: {ex.Message}";
        }
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            _syncing.RemoveAll(s => s.Device == device && s.Folder == folder);
            await LoadTakesAsync();
            var vm = Takes.Find(folder);
            ShowToast(error is null ? $"Downloaded from {device.Name}" : "Download needs attention",
                error ?? "", error is not null, vm);
        });
    }

    private static void TryDeleteEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(f => !f.EndsWith(TakeManifest.FileName)))
            {
                Directory.Delete(folder, true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- being controlled

    [RelayCommand]
    private void DisconnectController() => _remote?.Responder.Disconnect();

    [RelayCommand]
    private void AllowController() => AnswerApproval(true);

    [RelayCommand]
    private void DenyController() => AnswerApproval(false);

    private void AnswerApproval(bool allow)
    {
        if (allow && ApprovalRemember && _approvalId is { } id && !_trustedControllers.Contains(id))
        {
            _trustedControllers.Add(id);
            SaveSettings();
        }
        IsApprovalOpen = false;
        _approval?.TrySetResult(allow);
        _approval = null;
    }

    private Task<bool> AskApprovalAsync(HelloMsg controller)
    {
        if (_trustedControllers.Contains(controller.Id))
        {
            return Task.FromResult(true);
        }
        if (IsRecordingUi)
        {
            return Task.FromResult(false);
        }
        _approval?.TrySetResult(false);
        _approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvalId = controller.Id;
        ApprovalText = $"{controller.Name} wants to start and stop recording on this device and receive the files.";
        ApprovalRemember = false;
        IsApprovalOpen = true;
        return _approval.Task;
    }

    private void OnControllerChanged(RemoteControllerInfo? c)
    {
        if (c is null)
        {
            // An unanswered request from a controller that gave up.
            if (_approval is not null)
            {
                AnswerApproval(false);
            }
        }
        IsControlled = c is not null;
        IsControllerConnected = c?.Connected == true;
        ControllerText = c is null ? "" : c.Connected ? $"Controlled by {c.Name}" : $"{c.Name} disconnected";
        _host?.OnRecordingChanged(IsRecording || IsControlled);
        RefreshRecordState();
    }

    /// <summary>Starts a take requested by the controller at its scheduled instant; returns the take's clock start.</summary>
    private async Task<long> StartForControllerAsync(long startAt, bool video)
    {
        if (_hub is null)
        {
            throw new InvalidOperationException("Not ready.");
        }
        if (IsRecordingUi)
        {
            throw new InvalidOperationException($"{ThisDeviceName} is already recording.");
        }
        if (video != IsVideoMode && HasVideo)
        {
            IsVideoMode = video;
        }
        // Fail fast rather than connecting mics now: a connect can take seconds (or time out) and would miss the shared start.
        if (!_hub.HasStreamingMic)
        {
            throw new InvalidOperationException(Mikes.Count == 0
                ? "No microphone added on that device."
                : "No microphone is connected on that device. Use Connect mics first.");
        }
        // Video needs the camera; a late camera only shifts this device's start (recorded in the take manifest).
        for (var i = 0; IsVideoMode && !IsCameraReady && i < 100; i++)
        {
            await Task.Delay(50);
        }
        var wait = TimeSpan.FromTicks(startAt - MediaClock.Now);
        if (wait > TimeSpan.FromMilliseconds(20))
        {
            await Task.Delay(wait - TimeSpan.FromMilliseconds(15));
        }
        while (MediaClock.Now < startAt)
        {
            Thread.Yield();
        }
        Directory.CreateDirectory(RecordingRoot);
        _hub.StartRecordingAll(RecordingRoot);
        EnterRecordingUi();
        return _hub.RecordingStartTime;
    }

    /// <summary>The controlled side's link to the UI; every call is marshalled to the UI thread.</summary>
    private sealed class RemoteRecorder(MainViewModel vm) : IRemoteRecorder
    {
        public StatusMsg GetStatus() => vm._remoteStatus;

        public Task<bool> ApproveAsync(HelloMsg controller) =>
            Dispatcher.UIThread.InvokeAsync(() => vm.AskApprovalAsync(controller));

        public Task SetModeAsync(bool video) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (vm.IsRecordingUi)
            {
                return;
            }
            if (video) { vm.SetVideoModeCommand.Execute(null); } else { vm.SetAudioModeCommand.Execute(null); }
        }).GetTask();

        public Task ConnectMicsAsync() => Dispatcher.UIThread.InvokeAsync(vm.ConnectAllAsync);

        public Task<long> StartAsync(string takeId, long startAt, bool video) =>
            Dispatcher.UIThread.InvokeAsync(() => vm.StartForControllerAsync(startAt, video));

        public Task StopAsync(string takeId) => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (vm.IsRecording && !vm.IsStopping && vm._hub is not null)
            {
                await vm.StopRecordingAsync(vm._hub);
            }
        });

        public Task<long> PauseAsync(string takeId, long pauseAt) =>
            Dispatcher.UIThread.InvokeAsync(() => vm.PauseForController(pauseAt, true)).GetTask();

        public Task<long> ResumeAsync(string takeId, long resumeAt) =>
            Dispatcher.UIThread.InvokeAsync(() => vm.PauseForController(resumeAt, false)).GetTask();

        public void ControllerChanged(RemoteControllerInfo? controller) =>
            Dispatcher.UIThread.Post(() => vm.OnControllerChanged(controller));
    }
}
