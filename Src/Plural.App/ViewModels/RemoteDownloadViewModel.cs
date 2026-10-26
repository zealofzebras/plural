using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.Core.Remote;

namespace Plural.App.ViewModels;

public partial class RemoteDownloadChoice(string name, string deviceId) : ViewModelBase
{
    public string Name { get; } = name;
    public string DeviceId { get; } = deviceId;
    [ObservableProperty] public partial bool IsSelected { get; set; }
}

public partial class RemoteDownloadViewModel(MainViewModel main, string folder, string takeId, string deviceId, string name) : ViewModelBase
{
    private string _status = "pending";
    private bool _requested;
    private string? _error;
    public string Folder { get; } = folder;
    public string TakeId { get; } = takeId;
    public string DeviceId { get; } = deviceId;
    public string Name { get; } = name;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Detail { get; set; } = "";
    [ObservableProperty] public partial string ActionText { get; set; } = "Download";
    [ObservableProperty] public partial bool CanDownload { get; set; }

    public void Update(ManifestDevice device)
    {
        _status = device.Status;
        _requested = device.DownloadRequested;
        if (device.Errors.Count > 0) { _error = string.Join("; ", device.Errors); }
        Refresh();
    }

    public void Refresh()
    {
        var device = main.FindRemote(DeviceId);
        var connected = device?.State == RemoteDeviceState.Connected;
        var receiving = connected && _requested && device?.SyncProgress is not null;
        CanDownload = !IsBusy && !receiving && _status != "recording";
        ActionText = connected ? "Download" : "Reconnect & download";
        Detail = IsBusy ? "Searching for device..."
            : device?.State == RemoteDeviceState.Rejected ? device.Error ?? "Remote control declined. Retry to ask again."
            : _error is not null ? _error
            : device?.State == RemoteDeviceState.AwaitingApproval ? "Waiting for remote control approval"
            : _status == "recording" ? "Recording"
            : receiving ? $"Receiving files... {device!.SyncProgress:P0}"
            : !connected ? (_requested ? "Download requested · waiting for connection" : "Waiting for connection · download pending")
            : _requested ? "Download requested · waiting for files"
            : "Download pending";
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        IsBusy = true;
        _error = null;
        Refresh();
        try { await main.RequestRemoteDownloadAsync(this); }
        catch (Exception ex) { _error = ex.Message; }
        finally { IsBusy = false; Refresh(); }
    }

    partial void OnCanDownloadChanged(bool value) => DownloadCommand.NotifyCanExecuteChanged();
}