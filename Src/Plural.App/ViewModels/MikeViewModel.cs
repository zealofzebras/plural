using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.Core.Platform;
using Plural.Core.Protocol;
using Plural.Core.Session;

namespace Plural.App.ViewModels;

/// <summary>"None" or a real output device, for the per-mic output picker.</summary>
public sealed record OutputOption(AudioOutputDevice? Device)
{
    public string Name => Device is null ? "None (record only)" : (Device.IsVirtualCable ? "Cable: " : "") + Device.Name;
    public override string ToString() => Name;
}

public partial class MikeViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;
    private bool _applyingDeviceValues;
    private int _peakHoldTicks;

    public MikeChannel Channel { get; }
    private IMicSource Source => Channel.Source;
    /// <summary>Null for a local (OS) microphone.</summary>
    private MikeSession? Session => Channel.Session;

    public string Name => Source.DisplayName;

    /// <summary>SmartMikes have device settings, battery and headphone status; local mics only have the output picker.</summary>
    public bool IsSmartMike => Session is not null;
    public bool IsLocal => Session is null;
    public string MissingText => IsSmartMike ? "Not paired" : "Not connected";

    [ObservableProperty] public partial string StateText { get; set; } = "Off";
    [ObservableProperty] public partial bool IsConnecting { get; set; }
    [ObservableProperty] public partial bool IsLost { get; set; }
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial double Level { get; set; }
    [ObservableProperty] public partial double PeakHold { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsStreaming { get; set; }
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial string RecordText { get; set; } = "";
    [ObservableProperty] public partial bool HasBattery { get; set; }
    [ObservableProperty] public partial string BatteryText { get; set; } = "";
    [ObservableProperty] public partial double BatteryFill { get; set; }
    [ObservableProperty] public partial bool IsBatteryLow { get; set; }
    [ObservableProperty] public partial bool HasHeadphones { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool NotPaired { get; set; }
    [ObservableProperty] public partial bool CanRemove { get; set; } = true;
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial OutputOption? SelectedOutput { get; set; }

    // Mic gain and AGC are not reported by the device; defaults match its usual state (AGC on).
    [ObservableProperty] public partial int MicGain { get; set; } = 50;
    [ObservableProperty] public partial bool Agc { get; set; } = true;
    [ObservableProperty] public partial int MonitorVolume { get; set; } = 80;
    [ObservableProperty] public partial bool NoiseSuppression { get; set; } = true;

    public IReadOnlyList<OutputOption> Outputs => _owner.Outputs;
    public string OutputLabel => _owner.OutputLabel;

    public string ConnectButtonText => IsBusy ? "Connecting…" : IsStreaming ? "Disconnect" : "Connect";

    public MikeViewModel(MikeChannel channel, MainViewModel owner)
    {
        Channel = channel;
        _owner = owner;
        SelectedOutput = owner.Outputs.FirstOrDefault();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ConnectButtonText));

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveMikeAsync(this);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsStreaming || Source.State is MikeState.Ready or MikeState.Streaming)
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
        if (IsBusy || Source.State is MikeState.Connecting or MikeState.Ready or MikeState.Streaming)
        {
            return;
        }
        IsBusy = true;
        Error = null;
        try
        {
            if (Session is { } session)
            {
                await session.ConnectAsync();
                ApplyReportedSettings();
                session.Set(MikeSetting.Agc, Agc ? 1 : 0);
                if (!Agc)
                {
                    session.Set(MikeSetting.MicGain, MicGain);
                }
                session.StartAudio(AudioQuality.High);
            }
            else if (Channel.LocalMic is { } mic)
            {
                if (!await _owner.EnsureMicrophonePermissionAsync())
                {
                    Error = "Microphone permission is needed to use this mic.";
                    return;
                }
                await mic.ConnectAsync();
            }
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
            Channel.SetOutput(null);
            await Source.DisconnectAsync();
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private void ApplyReportedSettings()
    {
        if (Session is null)
        {
            return;
        }
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
        if (Source.State == MikeState.Streaming)
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
        if (_applyingDeviceValues || Session is null || Session.State is not (MikeState.Ready or MikeState.Streaming))
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
        var s = Source;
        IsStreaming = s.State == MikeState.Streaming;
        IsLost = s.State == MikeState.Failed;
        IsConnecting = !IsStreaming && (IsBusy || s.State is MikeState.Connecting or MikeState.Ready);
        StateText = IsStreaming ? "Live" : IsConnecting ? (s.State == MikeState.Ready ? "Ready" : "Connecting") : IsLost ? "Lost" : "Off";
        if (s.State == MikeState.Failed && s.LastError is { } e)
        {
            Error = e;
        }
        OnPropertyChanged(nameof(ConnectButtonText));
        IsRecording = Channel.RecordingPath is not null;
        CanRemove = !_owner.IsRecordingUi && !IsRecording;
        RecordText = IsRecording ? $"REC {Channel.RecordedDuration:mm\\:ss}" : "";

        if (Session?.BatteryPercent is { } b)
        {
            HasBattery = true;
            BatteryText = $"{b}%";
            BatteryFill = Math.Clamp(b, 0, 100) / 100.0 * 16;
            IsBatteryLow = b <= 20;
        }
        else
        {
            HasBattery = false;
        }
        HasHeadphones = Session?.HeadphonesPlugged == true;

        // Peak meter with a dB-ish scale (-60 dBFS … 0 dBFS → 0 … 100).
        var peak = IsStreaming ? s.PeakLevel : 0f;
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
