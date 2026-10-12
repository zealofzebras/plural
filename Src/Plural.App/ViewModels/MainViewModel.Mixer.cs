using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.App.Services;
using Plural.Core.Session;

namespace Plural.App.ViewModels;

/// <summary>
/// Mixer mode: a channel strip per mic (device gain/AGC/NS/phones, direct output, fader, mute, post-fader meter) and a master
/// strip that plays the mix of all mics to one output. Faders and mutes act on the recording and the live outputs immediately,
/// also while recording; the mix itself is live output only (every mic still records to its own track).
/// </summary>
public partial class MainViewModel
{
    private readonly MeterState _mixMeter = new();
    private Dictionary<string, SavedMix> _savedMix = new(StringComparer.Ordinal);
    private string? _savedMixOutputId;
    private long _mixSaveAt;

    [ObservableProperty] public partial bool IsMixerMode { get; set; }
    [ObservableProperty] public partial bool ShowMixerChip { get; set; } = true;
    [ObservableProperty] public partial OutputOption? SelectedMixOutput { get; set; }
    [ObservableProperty] public partial double MixFaderDb { get; set; }
    [ObservableProperty] public partial bool MixMuted { get; set; }
    [ObservableProperty] public partial string MixFaderText { get; set; } = "0.0 dB";
    [ObservableProperty] public partial double MixLevel { get; set; }
    [ObservableProperty] public partial double MixPeakHold { get; set; }
    [ObservableProperty] public partial string? MixError { get; set; }

    /// <summary>Outputs for the master strip ("Off" plus every device).</summary>
    public IReadOnlyList<OutputOption> MixOutputs { get; private set; } = [new OutputOption(null, "Off")];

    public bool IsCardsMode => !IsMixerMode;

    /// <summary>The compact recording strip belongs to the cards layout; the mixer has its own meters.</summary>
    public bool ShowStrip => StripVisible && !IsMixerMode;
    public bool HasMixError => MixError is not null;

    internal SavedMix? SavedMixFor(string id) => _savedMix.GetValueOrDefault(id);

    [RelayCommand]
    private void ToggleMixer()
    {
        // Video takes keep their layout; audio takes can switch between cards and mixer at any time.
        if (IsRecordingUi && IsVideoMode)
        {
            return;
        }
        IsMixerMode = !IsMixerMode;
    }

    partial void OnIsMixerModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCardsMode));
        OnPropertyChanged(nameof(ShowStrip));
        if (value && IsVideoMode && !IsRecordingUi)
        {
            IsVideoMode = false;
        }
        UpdateLayoutState();
        SaveSettings();
    }

    partial void OnSelectedMixOutputChanged(OutputOption? value)
    {
        if (_hub is null)
        {
            return;
        }
        // Re-selecting the same device after the output list is rebuilt must not reopen it.
        if (!Equals(value?.Device, _hub.Mix.Output))
        {
            try
            {
                _hub.Mix.SetOutput(value?.Device);
                MixError = null;
            }
            catch (Exception ex)
            {
                MixError = ex.Message;
            }
        }
        if (value is not null)
        {
            _savedMixOutputId = value.Device?.Id;
            SaveSettings();
        }
    }

    partial void OnStripVisibleChanged(bool value) => OnPropertyChanged(nameof(ShowStrip));

    partial void OnMixErrorChanged(string? value) => OnPropertyChanged(nameof(HasMixError));

    partial void OnMixFaderDbChanged(double value)
    {
        if (_hub is not null)
        {
            _hub.Mix.FaderDb = value;
        }
        MixFaderText = MeterState.FormatDb(Math.Max(value, MikeChannel.MinFaderDb));
        ScheduleMixSave();
    }

    partial void OnMixMutedChanged(bool value)
    {
        if (_hub is not null)
        {
            _hub.Mix.Muted = value;
        }
        ScheduleMixSave();
    }

    /// <summary>Faders move continuously: save once they rest for a moment instead of on every step.</summary>
    internal void ScheduleMixSave()
    {
        if (!_loading)
        {
            _mixSaveAt = Environment.TickCount64 + 600;
        }
    }

    private void LoadMixSettings(AppSettings s)
    {
        _savedMix = s.Mix.Where(m => m.Id.Length > 0).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        _savedMixOutputId = s.MixOutputId;
        MixFaderDb = s.MixFaderDb;
        MixMuted = s.MixMuted;
        IsMixerMode = s.MixerMode && !IsVideoMode;
    }

    private void SaveMixSettings(AppSettings s)
    {
        foreach (var m in Mikes)
        {
            _savedMix[m.Channel.Source.Id] = new SavedMix { Id = m.Channel.Source.Id, FaderDb = m.FaderDb, Muted = m.Muted };
        }
        s.MixerMode = IsMixerMode;
        s.Mix = [.. _savedMix.Values.Where(m => m.FaderDb != 0 || m.Muted)];
        s.MixOutputId = _savedMixOutputId;
        s.MixFaderDb = MixFaderDb;
        s.MixMuted = MixMuted;
    }

    /// <summary>Rebuilds the master strip's output list after the device list changed, keeping (or restoring) the chosen output.</summary>
    private void RefreshMixOutputs()
    {
        MixOutputs = [new OutputOption(null, "Off"), .. Outputs.Where(o => o.Device is not null)];
        OnPropertyChanged(nameof(MixOutputs));
        SelectedMixOutput = MixOutputs.FirstOrDefault(o => o.Device?.Id == _savedMixOutputId) ?? MixOutputs[0];
    }

    private void TickMixer()
    {
        if (_mixSaveAt > 0 && Environment.TickCount64 >= _mixSaveAt)
        {
            _mixSaveAt = 0;
            SaveSettings();
        }
        if (IsMixerMode && _hub is not null)
        {
            _mixMeter.Update(_hub.Mix.Output is null ? 0f : _hub.Mix.TakePeak());
            MixLevel = _mixMeter.Level;
            MixPeakHold = _mixMeter.Peak;
        }
    }
}
