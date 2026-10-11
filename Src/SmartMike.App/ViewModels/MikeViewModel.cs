using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartMike.Core.Platform;
using SmartMike.Core.Protocol;
using SmartMike.Core.Session;

namespace SmartMike.App.ViewModels;

/// <summary>"None" or a real output device, for the per-mic output picker.</summary>
public sealed record OutputOption(AudioOutputDevice? Device)
{
    public string Name => Device is null ? "None (record only)" : (Device.IsVirtualCable ? "🔌 " : "") + Device.Name;
    public override string ToString() => Name;
}

public partial class MikeViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;
    private bool _applyingDeviceValues;

    public MikeChannel Channel { get; }
    private MikeSession Session => Channel.Session;

    public string Name => Session.DisplayName;
    public string Address => Session.Device.Address.ToString();

    [ObservableProperty] public partial string StateText { get; set; } = "Disconnected";
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial double Level { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsStreaming { get; set; }
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial OutputOption? SelectedOutput { get; set; }

    // Mic gain and AGC are not reported by the device; defaults match its usual state (AGC on).
    [ObservableProperty] public partial int MicGain { get; set; } = 50;
    [ObservableProperty] public partial bool Agc { get; set; } = true;
    [ObservableProperty] public partial int MonitorVolume { get; set; } = 80;
    [ObservableProperty] public partial bool NoiseSuppression { get; set; } = true;

    public IReadOnlyList<OutputOption> Outputs => _owner.Outputs;
    public string OutputLabel => _owner.OutputLabel;

    public string ConnectButtonText => IsStreaming ? "Disconnect" : "Connect";

    public MikeViewModel(MikeChannel channel, MainViewModel owner)
    {
        Channel = channel;
        _owner = owner;
        SelectedOutput = owner.Outputs.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsStreaming || Session.State is MikeState.Ready or MikeState.Streaming)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    public async Task ConnectAsync()
    {
        if (IsBusy || Session.State is MikeState.Ready or MikeState.Streaming)
        {
            return;
        }
        IsBusy = true;
        Error = null;
        try
        {
            await Session.ConnectAsync();
            ApplyReportedSettings();
            Session.Set(MikeSetting.Agc, Agc ? 1 : 0);
            if (!Agc)
            {
                Session.Set(MikeSetting.MicGain, MicGain);
            }
            Session.StartAudio(AudioQuality.High);
            Channel.SetOutput(SelectedOutput?.Device);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    public async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            Channel.StopRecording();
            Channel.SetOutput(null);
            await Session.DisconnectAsync();
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void ApplyReportedSettings()
    {
        _applyingDeviceValues = true;
        try
        {
            var s = Session.Settings;
            if (s.TryGetValue(MikeSetting.MonitorVolume, out var mon))
            {
                MonitorVolume = mon;
            }
            if (s.TryGetValue(MikeSetting.NoiseSuppression, out var ns))
            {
                NoiseSuppression = ns != 0;
            }
        }
        finally
        {
            _applyingDeviceValues = false;
        }
    }

    partial void OnSelectedOutputChanged(OutputOption? value)
    {
        if (Session.State == MikeState.Streaming)
        {
            Channel.SetOutput(value?.Device);
        }
    }

    partial void OnMicGainChanged(int value) => Send(MikeSetting.MicGain, value);
    partial void OnAgcChanged(bool value) => Send(MikeSetting.Agc, value ? 1 : 0);
    partial void OnMonitorVolumeChanged(int value) => Send(MikeSetting.MonitorVolume, value);
    partial void OnNoiseSuppressionChanged(bool value) => Send(MikeSetting.NoiseSuppression, value ? 1 : 0);

    private void Send(MikeSetting setting, int value)
    {
        if (_applyingDeviceValues || Session.State is not (MikeState.Ready or MikeState.Streaming))
        {
            return;
        }
        try
        {
            Session.Set(setting, value);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Polled by the main view model's UI timer.</summary>
    public void Refresh()
    {
        var s = Session;
        StateText = s.State switch
        {
            MikeState.Streaming => "● Live",
            MikeState.Ready => "Connected",
            MikeState.Connecting => "Connecting…",
            MikeState.Failed => "Connection lost",
            _ => "Disconnected",
        };
        if (s.State == MikeState.Failed && s.LastError is { } e)
        {
            Error = e;
        }
        IsStreaming = s.State == MikeState.Streaming;
        OnPropertyChanged(nameof(ConnectButtonText));
        IsRecording = Channel.RecordingPath is not null;

        var parts = new List<string>();
        if (s.Version is { } v)
        {
            parts.Add($"FW {v.Firmware}");
        }
        if (s.BatteryPercent is { } b)
        {
            parts.Add($"Battery {b}%");
        }
        if (s.HeadphonesPlugged == true)
        {
            parts.Add("🎧");
        }
        if (IsRecording)
        {
            parts.Add($"REC {Channel.RecordedDuration:mm\\:ss}");
        }
        Details = string.Join("  ·  ", parts);

        // Peak meter with a dB-ish scale (−60 dBFS … 0 dBFS → 0 … 100).
        var peak = IsStreaming ? s.PeakLevel : 0f;
        var db = peak > 0 ? 20 * Math.Log10(peak) : -60;
        Level = Math.Clamp((db + 60) / 60 * 100, 0, 100);
    }
}
