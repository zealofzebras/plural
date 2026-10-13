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
public sealed record OutputOption(AudioOutputDevice? Device, string? NoneLabel = null)
{
    public string Name => Device is null ? NoneLabel ?? "None (record only)" : (Device.IsVirtualCable ? "Cable: " : "") + Device.Name;

    /// <summary>For the mixer's narrow pickers.</summary>
    public string ShortName => Device?.Name ?? "Off";
    public override string ToString() => Name;
}

public partial class MikeViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;
    private bool _applyingDeviceValues;
    private readonly MeterState _meter = new();
    private readonly MeterState _mixMeter = new();

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
    /// <summary>The SmartMike link dropped and is being restored (amber); a take keeps recording and fills the gap with silence.</summary>
    [ObservableProperty] public partial bool IsReconnecting { get; set; }
    /// <summary>"Reconnecting… 0:12" for the card and the mixer strip's banner.</summary>
    /// <summary>Short state for the 108 dp mixer strip ("Retry" instead of the long reconnect text).</summary>
    [ObservableProperty] public partial string StripStateText { get; set; } = "Off";
    [ObservableProperty] public partial string ReconnectClock { get; set; } = "";
    [ObservableProperty] public partial string ReconnectText { get; set; } = "";
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

    // Mixer: channel fader and mute (applied to the recording and every live output), post-fader meter.
    [ObservableProperty] public partial double FaderDb { get; set; }
    [ObservableProperty] public partial bool Muted { get; set; }
    [ObservableProperty] public partial string FaderText { get; set; } = "0.0 dB";
    [ObservableProperty] public partial double MixLevel { get; set; }
    [ObservableProperty] public partial double MixPeakHold { get; set; }

    public IReadOnlyList<OutputOption> Outputs => _owner.Outputs;
    public string OutputLabel => _owner.OutputLabel;

    /// <summary>Battery of a connected SmartMike in percent; null for local mics or before the first report.</summary>
    public int? BatteryPercent { get; private set; }

    public string ConnectButtonText => IsBusy ? "Connecting…" : IsStreaming || IsReconnecting ? "Disconnect" : "Connect";

    public MikeViewModel(MikeChannel channel, MainViewModel owner)
    {
        Channel = channel;
        _owner = owner;
        SelectedOutput = owner.Outputs.FirstOrDefault();
        if (owner.SavedMixFor(channel.Source.Id) is { } mix)
        {
            FaderDb = mix.FaderDb;
            Muted = mix.Muted;
        }
    }

    partial void OnFaderDbChanged(double value)
    {
        Channel.FaderDb = value;
        FaderText = MeterState.FormatDb(Channel.FaderDb);
        _owner.ScheduleMixSave();
    }

    partial void OnMutedChanged(bool value)
    {
        Channel.Muted = value;
        _owner.ScheduleMixSave();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ConnectButtonText));

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveMikeAsync(this);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsStreaming || Source.State is MikeState.Ready or MikeState.Streaming or MikeState.Reconnecting)
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
        if (IsBusy || Source.State is MikeState.Connecting or MikeState.Ready or MikeState.Streaming or MikeState.Reconnecting)
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
    partial void OnAgcChanged(bool value)
    {
        _owner.UpdateGainKnobs();
        Send(MikeSetting.Agc, value ? 1 : 0);
    }
    partial void OnMonitorVolumeChanged(int value) => Send(MikeSetting.MonitorVolume, value);
    partial void OnNoiseSuppressionChanged(bool value) => Send(MikeSetting.NoiseSuppression, value ? 1 : 0);

    private void Send(MikeSetting setting, int value)
    {
        if (_applyingDeviceValues || Session is null || Session.State is not (MikeState.Ready or MikeState.Streaming or MikeState.Reconnecting))
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

    /// <summary>"Reconnecting… 0:12" while a SmartMike link is being restored.</summary>
    private string ReconnectingText()
    {
        var gap = Session?.ReconnectingSince is { } since ? DateTimeOffset.UtcNow - since : TimeSpan.Zero;
        if (gap < TimeSpan.Zero) { gap = TimeSpan.Zero; }
        return $"Reconnecting… {(int)gap.TotalMinutes}:{gap.Seconds:00}";
    }

    /// <summary>Polled by the main view model's UI timer.</summary>
    public void Refresh()
    {
        var s = Source;
        IsStreaming = s.State == MikeState.Streaming;
        IsLost = s.State == MikeState.Failed;
        IsReconnecting = s.State == MikeState.Reconnecting;
        IsConnecting = !IsStreaming && (IsBusy || s.State is MikeState.Connecting or MikeState.Ready or MikeState.Reconnecting);
        ReconnectText = IsReconnecting ? ReconnectingText() : "";
        ReconnectClock = IsReconnecting ? ReconnectText[(ReconnectText.IndexOf(' ') + 1)..] : "";
        StateText = IsStreaming ? "Live" : IsReconnecting ? "Retrying" : IsConnecting ? (s.State == MikeState.Ready ? "Ready" : "Connecting") : IsLost ? "Lost" : "Off";
        StripStateText = IsReconnecting ? "Retry" : StateText;
        if (s.State == MikeState.Failed && s.LastError is { } e)
        {
            Error = e;
        }
        OnPropertyChanged(nameof(ConnectButtonText));
        IsRecording = Channel.RecordingPath is not null;
        CanRemove = !_owner.IsRecordingUi && !IsRecording;
        RecordText = IsRecording ? $"REC {Channel.RecordedDuration:mm\\:ss}" : "";

        BatteryPercent = Session?.BatteryPercent;
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

        // Input meter (cards) and post-fader meter (mixer), dB-ish scale (-60 dBFS … 0 dBFS → 0 … 100).
        _meter.Update(IsStreaming ? s.PeakLevel : 0f);
        Level = _meter.Level;
        PeakHold = _meter.Peak;
        _mixMeter.Update(IsStreaming ? Channel.PostFaderPeak : 0f);
        MixLevel = _mixMeter.Level;
        MixPeakHold = _mixMeter.Peak;
    }
}
