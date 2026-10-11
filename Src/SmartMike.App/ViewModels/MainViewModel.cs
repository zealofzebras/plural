using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartMike.App.Services;
using SmartMike.Core.Session;

namespace SmartMike.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IAppHost? _host;
    private readonly MikeHub? _hub;
    private bool _initialized;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _recordClock = new();

    public ObservableCollection<MikeViewModel> Mikes { get; } = [];
    public IReadOnlyList<OutputOption> Outputs { get; private set; } = [new OutputOption(null)];

    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial string RecordingTime { get; set; } = "00:00";
    [ObservableProperty] public partial string RecordingRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SmartMike Recordings");
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool HasMikes { get; set; }

    public string RecordButtonText => IsRecording ? "■  Stop recording" : "●  Record all";
    public string OutputLabel => _host?.OutputLabel ?? "Live output";
    public string RecordingsActionText => _host?.RecordingsActionText ?? "Open folder";
    public bool CanChangeRecordingRoot => _host?.CanChangeRecordingRoot ?? true;

    /// <summary>Design-time constructor.</summary>
    public MainViewModel()
    {
        _timer = new DispatcherTimer();
    }

    public MainViewModel(IAppHost host)
    {
        _host = host;
        _hub = host.CreateHub();
        RecordingRoot = host.RecordingRoot;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    /// <summary>Called when the main view is shown; scans once (permissions need a visible UI on Android).</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        await ScanAsync();
    }

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordButtonText));
        _host?.OnRecordingChanged(value);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (_hub is null || _host is null || IsScanning)
        {
            return;
        }
        IsScanning = true;
        try
        {
            if (!await _host.EnsurePermissionsAsync())
            {
                Status = "Bluetooth permission is needed to find paired SmartMikes.";
                return;
            }
            ScanCore(_hub);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ScanCore(MikeHub hub)
    {
        Outputs = [new OutputOption(null), .. hub.Audio.GetOutputDevices().Select(d => new OutputOption(d))];
        OnPropertyChanged(nameof(Outputs));

        var found = hub.FindSmartMikes();
        foreach (var dev in found)
        {
            if (Mikes.All(m => m.Channel.Session.Device.Address != dev.Address))
            {
                Mikes.Add(new MikeViewModel(hub.Add(dev), this));
            }
        }
        HasMikes = Mikes.Count > 0;
        var cables = Outputs.Count(o => o.Device?.IsVirtualCable == true);
        Status = OperatingSystem.IsWindows()
            ? $"{found.Count} paired SmartMike(s) · {cables} virtual cable(s) found" +
              (cables == 0 ? " — install a virtual cable (e.g. VB-CABLE) to use mics in other apps" : "")
            : $"{found.Count} paired SmartMike(s) found";
    }

    [RelayCommand]
    private Task ConnectAllAsync() => Task.WhenAll(Mikes.Select(m => m.ConnectAsync()));

    [RelayCommand]
    private void ToggleRecording()
    {
        if (_hub is null)
        {
            return;
        }
        if (IsRecording)
        {
            var files = _hub.StopRecordingAll();
            _recordClock.Reset();
            IsRecording = false;
            Status = $"Saved {files.Count} file(s) to {_hub.RecordingFolder}";
        }
        else
        {
            if (!_hub.Channels.Any(c => c.Session.State == MikeState.Streaming))
            {
                Status = "Connect at least one mic before recording.";
                return;
            }
            var folder = _hub.StartRecordingAll(RecordingRoot);
            _recordClock.Restart();
            IsRecording = true;
            Status = $"Recording to {folder}";
        }
    }

    [RelayCommand]
    private void OpenRecordings()
    {
        var folder = _hub?.RecordingFolder ?? RecordingRoot;
        Directory.CreateDirectory(folder);
        try
        {
            _host?.ShowRecordings(folder);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private void Tick()
    {
        foreach (var m in Mikes)
        {
            m.Refresh();
        }
        if (IsRecording)
        {
            RecordingTime = _recordClock.Elapsed.ToString(@"mm\:ss");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }
    }
}
