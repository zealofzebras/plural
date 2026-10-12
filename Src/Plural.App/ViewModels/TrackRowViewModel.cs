using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.App.Controls;
using Plural.Core.Library;
using Plural.Core.Playback;

namespace Plural.App.ViewModels;

/// <summary>One audio track row of the Player page: name, mute / solo / gain, and the waveform view state of the shared timeline.</summary>
public partial class TrackRowViewModel : ViewModelBase
{
    private static readonly IBrush[] Palette = new[]
    {
        "#4C8DFF", "#35D07F", "#F5B53D", "#FF6B9A", "#9B7BFF", "#2EC4D6", "#FF8A4C", "#B6D94C",
    }.Select(c => (IBrush)new SolidColorBrush(Color.Parse(c))).ToArray();

    private readonly PlayerViewModel _owner;
    private bool _loading = true;

    public TrackRowViewModel(PlayerViewModel owner, PlayerTrack track, int index)
    {
        _owner = owner;
        Track = track;
        Brush = Palette[index % Palette.Length];
        Name = track.Name.Length > 0 ? track.Name : $"Track {index + 1}";
        IsRemote = track.Info.IsRemote;
        DeviceText = IsRemote ? track.Device : "";
        GainDb = track.GainDb;
        IsMuted = track.Mute;
        IsSolo = track.Solo;
        CanPlay = track.CanPlay;
        Problem = track.Problem ?? "";
        _loading = false;
        UpdateGainText();
    }

    public PlayerTrack Track { get; }
    public IBrush Brush { get; }
    public string Name { get; }
    public bool IsRemote { get; }
    public string DeviceText { get; }
    public bool HasDevice => DeviceText.Length > 0;
    public string DeviceSuffix => HasDevice ? "  ·  " + DeviceText : "";
    public bool CanPlay { get; }
    public bool CantPlay => !CanPlay;
    public string Problem { get; }

    /// <summary>Shown over the waveform when the track cannot be played here.</summary>
    public string CantPlayText => "Can't play this track here";

    [ObservableProperty] public partial PeakReader? Peaks { get; set; }
    [ObservableProperty] public partial double WaveScale { get; set; } = 1;
    [ObservableProperty] public partial long StartSample { get; set; }
    [ObservableProperty] public partial double SamplesPerPixel { get; set; }
    [ObservableProperty] public partial IReadOnlyList<WaveRange>? Dropouts { get; set; }
    [ObservableProperty] public partial IReadOnlyList<long>? Pauses { get; set; }

    [ObservableProperty] public partial bool IsMuted { get; set; }
    [ObservableProperty] public partial bool IsSolo { get; set; }
    [ObservableProperty] public partial double GainDb { get; set; }
    [ObservableProperty] public partial string GainText { get; set; } = "0.0 dB";
    [ObservableProperty] public partial bool Dimmed { get; set; }
    [ObservableProperty] public partial bool IsFocusedRow { get; set; }
    [ObservableProperty] public partial bool IsGainOpen { get; set; }
    [ObservableProperty] public partial bool IsAnalyzing { get; set; }

    // Set by the owner when the form factor changes.
    [ObservableProperty] public partial double RowHeight { get; set; } = 64;
    [ObservableProperty] public partial double HeaderWidth { get; set; } = 252;
    [ObservableProperty] public partial bool IsCompact { get; set; }

    public bool IsWideRow => !IsCompact;
    public double WaveOpacity => Dimmed ? 0.3 : 1;
    public bool HasPeaks => Peaks is not null;
    public bool ShowAnalyzing => IsAnalyzing && Peaks is null && CanPlay;
    public bool ShowNoWaveform => Peaks is null && !IsAnalyzing && CanPlay;

    partial void OnPeaksChanged(PeakReader? value)
    {
        OnPropertyChanged(nameof(HasPeaks));
        OnPropertyChanged(nameof(ShowAnalyzing));
        OnPropertyChanged(nameof(ShowNoWaveform));
        if (value is not null)
        {
            WaveScale = ScaleFor(value);
            BuildMarkers();
        }
        _owner.RefreshRows();
    }

    partial void OnIsAnalyzingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAnalyzing));
        OnPropertyChanged(nameof(ShowNoWaveform));
    }

    partial void OnIsGainOpenChanged(bool value) => _owner.UpdateGainOpen();
    partial void OnDimmedChanged(bool value) => OnPropertyChanged(nameof(WaveOpacity));
    partial void OnIsCompactChanged(bool value) => OnPropertyChanged(nameof(IsWideRow));

    partial void OnIsMutedChanged(bool value)
    {
        if (!_loading) { _owner.SetMute(this, value); }
    }

    partial void OnIsSoloChanged(bool value)
    {
        if (!_loading) { _owner.SetSolo(this, value); }
    }

    partial void OnGainDbChanged(double value)
    {
        UpdateGainText();
        if (!_loading) { _owner.SetGain(this, value); }
    }

    private void UpdateGainText() =>
        GainText = GainDb <= -59.9 ? "-inf" : (GainDb > 0 ? "+" : "") + GainDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB";

    [RelayCommand] private void ToggleMute() => IsMuted = !IsMuted;
    [RelayCommand] private void ToggleSolo() => IsSolo = !IsSolo;
    [RelayCommand] private void ToggleGain() => IsGainOpen = !IsGainOpen;
    [RelayCommand] private void CloseGain() => IsGainOpen = false;
    [RelayCommand] private void ResetGain() => GainDb = 0;
    [RelayCommand] private void Focus() => _owner.FocusRow(this);

    /// <summary>Re-reads mute / solo / gain from the player track without writing back.</summary>
    public void Sync()
    {
        _loading = true;
        IsMuted = Track.Mute;
        IsSolo = Track.Solo;
        GainDb = Track.GainDb;
        _loading = false;
    }

    /// <summary>Updates the waveform window for the shared timeline view (seconds, dip width of the waveform area).</summary>
    public void UpdateView(double viewStartSec, double viewSpanSec, double width)
    {
        var rate = Peaks?.SampleRate > 0 ? Peaks.SampleRate : TakePlayer.SampleRate;
        var offset = Track.Offset.TotalSeconds;
        StartSample = (long)Math.Round((viewStartSec - offset) * rate);
        SamplesPerPixel = width > 1 ? Math.Max(0.5, viewSpanSec * rate / width) : 0;
    }

    private void BuildMarkers()
    {
        var take = _owner.Take;
        if (take is null || Peaks is null) { return; }
        var rate = Peaks.SampleRate > 0 ? Peaks.SampleRate : TakePlayer.SampleRate;
        var offsetMs = Track.Offset.TotalMilliseconds;
        var drops = take.Dropouts.Where(d => string.Equals(d.Track, Track.Id, StringComparison.Ordinal))
            .Select(d => new WaveRange((long)(d.StartMs * rate / 1000), (long)(d.EndMs * rate / 1000))).ToList();
        Dropouts = drops.Count > 0 ? drops : null;
        var pauses = take.Pauses.Select(p => (long)((p.AtMs - offsetMs) * rate / 1000)).ToList();
        Pauses = pauses.Count > 0 ? pauses : null;
    }

    /// <summary>Gain that brings the loudest part close to full height (quiet mics are still readable), at most 8x.</summary>
    private static double ScaleFor(PeakReader r)
    {
        const int columns = 256;
        if (r.SampleCount < columns) { return 1; }
        var min = new short[columns];
        var max = new short[columns];
        r.GetRange(0, (double)r.SampleCount / columns, min, max);
        var peak = 0;
        for (var i = 0; i < columns; i++) { peak = Math.Max(peak, Math.Max(Math.Abs((int)max[i]), Math.Abs((int)min[i]))); }
        return peak < 64 ? 1 : Math.Clamp(0.92 * 32768 / peak, 1, 8);
    }
}
