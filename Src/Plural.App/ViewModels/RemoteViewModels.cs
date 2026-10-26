using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.Core.Remote;

namespace Plural.App.ViewModels;

/// <summary>An app found by a search (any transport); tap to control it.</summary>
public partial class PeerViewModel(DiscoveredPeer peer, MainViewModel owner) : ViewModelBase
{
    public DiscoveredPeer Peer { get; set; } = peer;
    public string Name => Peer.Name;
    public string Detail => Peer.Platform;

    [ObservableProperty] public partial bool IsAdded { get; set; }

    [RelayCommand]
    private void Connect() => owner.ConnectPeer(this);

    public void Update(DiscoveredPeer p)
    {
        Peer = p;
        OnPropertyChanged(nameof(Name));
    }
}

/// <summary>A remote this app controls: state, its mics' levels, mode and file sync.</summary>
public partial class RemoteDeviceViewModel(RemoteDevice device, MainViewModel owner) : ViewModelBase
{
    private int _peakHoldTicks;

    public RemoteDevice Device { get; } = device;

    [ObservableProperty] public partial string Name { get; set; } = device.Name;
    [ObservableProperty] public partial string StateText { get; set; } = "Connecting";
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial bool IsLive { get; set; }
    [ObservableProperty] public partial bool IsConnecting { get; set; } = true;
    [ObservableProperty] public partial bool IsLost { get; set; }
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial bool IsPaused { get; set; }
    /// <summary>"Paused" or "Doesn't support pause" while a take runs on this device (amber); empty otherwise.</summary>
    [ObservableProperty] public partial string RecordingNote { get; set; } = "";
    [ObservableProperty] public partial bool IsVideo { get; set; }
    [ObservableProperty] public partial bool HasVideo { get; set; }
    [ObservableProperty] public partial double Level { get; set; }
    [ObservableProperty] public partial double PeakHold { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string SyncText { get; set; } = "";
    [ObservableProperty] public partial bool IsSyncing { get; set; }
    [ObservableProperty] public partial bool CanChange { get; set; }
    [ObservableProperty] public partial string DownloadNote { get; set; } = "";

    public string ModeText => IsVideo ? "Video" : "Audio";

    partial void OnIsVideoChanged(bool value) => OnPropertyChanged(nameof(ModeText));

    [RelayCommand]
    private Task ToggleModeAsync() => Device.SetModeAsync(!IsVideo);

    [RelayCommand]
    private Task ConnectMicsAsync() => Device.ConnectMicsAsync();

    [RelayCommand]
    private Task DisconnectAsync() => owner.DisconnectRemoteAsync(this);

    /// <summary>Polled by the main view model's UI timer.</summary>
    public void Refresh(bool recordingUi)
    {
        var d = Device;
        var st = d.Status;
        Name = d.Name;
        IsLive = d.State == RemoteDeviceState.Connected;
        DownloadNote = IsLive && !d.SupportsDeferredDownloads
            ? "Older Plural version: recordings download automatically; postponing is unavailable." : "";
        IsConnecting = d.State is RemoteDeviceState.Connecting or RemoteDeviceState.AwaitingApproval;
        IsLost = d.State is RemoteDeviceState.Reconnecting or RemoteDeviceState.Rejected;
        IsRecording = IsLive && st?.Recording == true;
        IsPaused = IsRecording && st?.Paused == true;
        RecordingNote = !IsRecording ? "" : IsPaused ? "Paused" : !d.SupportsPause ? "Doesn't support pause" : "";
        HasVideo = st?.HasVideo ?? d.HasVideo;
        IsVideo = st?.VideoMode == true;
        CanChange = IsLive && !recordingUi && st?.Recording != true;
        Error = d.State is RemoteDeviceState.Reconnecting or RemoteDeviceState.Rejected ? d.Error : null;

        var sync = d.SyncProgress;
        IsSyncing = sync is not null;
        SyncText = sync is { } p ? $"Receiving files… {p:P0}" : "";
        StateText = d.State switch
        {
            RemoteDeviceState.Connecting => "Connecting",
            RemoteDeviceState.AwaitingApproval => "Waiting",
            RemoteDeviceState.Reconnecting => "Lost",
            RemoteDeviceState.Rejected => "Declined",
            RemoteDeviceState.Disconnected => "Off",
            _ when IsSyncing => "Syncing",
            _ when IsPaused => "Paused",
            _ when IsRecording => "REC",
            _ => "Live",
        };

        if (d.State == RemoteDeviceState.AwaitingApproval)
        {
            Details = $"Waiting for {d.Name} to allow control";
        }
        else if (st is null)
        {
            Details = "";
        }
        else
        {
            var live = st.Mics.Count(m => m.Live);
            var mics = st.Mics.Count == 0 ? "no mics" : $"{live} of {st.Mics.Count} mic{(st.Mics.Count == 1 ? "" : "s")} live";
            var mode = st.VideoMode ? (st.CameraReady ? "video" : "video, camera starting") : "audio";
            Details = $"{mics} · {mode}{(st.Format is { } f ? " · " + f : "")}";
        }

        var peak = IsLive && st is not null && st.Mics.Count > 0 ? st.Mics.Max(m => m.Level) : 0f;
        var db = peak > 0 ? 20 * Math.Log10(peak) : -60;
        Level = Math.Clamp((db + 60) / 60 * 100, 0, 100);
        if (Level >= PeakHold)
        {
            PeakHold = Level;
            _peakHoldTicks = 12;
        }
        else if (_peakHoldTicks > 0)
        {
            _peakHoldTicks--;
        }
        else
        {
            PeakHold = Math.Max(Level, PeakHold - 3);
        }
    }
}
