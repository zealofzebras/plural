using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plural.Core.Library;
using Plural.Core.Recording;
using Plural.Core.Remote;

namespace Plural.App.ViewModels;

/// <summary>One track line in the detail pane.</summary>
public sealed record TrackLine(string Name, string Detail);

/// <summary>One take of the library as the Takes page shows it (a list row, the detail pane, a toast target).</summary>
public partial class TakeViewModel : ViewModelBase
{
    private readonly TakesViewModel _owner;
    private string? _searchText;

    [ObservableProperty] public partial TakeInfo Info { get; set; }
    [ObservableProperty] public partial Bitmap? Poster { get; set; }
    [ObservableProperty] public partial PeakReader? WavePeaks { get; set; }
    [ObservableProperty] public partial double WaveScale { get; set; } = 1;
    [ObservableProperty] public partial bool IsChecked { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>The page is in multi-select mode: rows show a check circle.</summary>
    [ObservableProperty] public partial bool SelectMode { get; set; }

    public bool HasThumb => Poster is not null || WavePeaks is not null;

    partial void OnPosterChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumb));
    partial void OnWavePeaksChanged(PeakReader? value) => OnPropertyChanged(nameof(HasThumb));
    partial void OnIsCheckedChanged(bool value) => _owner.UpdateChecked();

    public TakeViewModel(TakeInfo info, TakesViewModel owner)
    {
        Info = info;
        _owner = owner;
        UpdateRemoteDownloads();
    }

    public ObservableCollection<RemoteDownloadViewModel> RemoteDownloads { get; } = [];

    private void UpdateRemoteDownloads()
    {
        var manifest = TakeManifest.Load(Folder);
        var pending = manifest?.Devices.Where(device => device.Role == "remote" && device.Status != "synced").ToList() ?? [];
        foreach (var gone in RemoteDownloads.Where(item => pending.All(device => device.Id != item.DeviceId)).ToList())
        {
            RemoteDownloads.Remove(gone);
        }
        foreach (var device in pending)
        {
            var item = RemoteDownloads.FirstOrDefault(item => item.DeviceId == device.Id);
            if (item is null)
            {
                item = new RemoteDownloadViewModel(_owner.Main, Folder, manifest!.TakeId, device.Id, device.Name);
                RemoteDownloads.Add(item);
            }
            item.Update(device);
        }
    }

    public string Folder => Info.Folder;
    public bool IsVideo => Info.HasVideo;
    public bool IsAudio => !Info.HasVideo;
    public bool IsRemote => Info.IsRemote;

    public bool CanShare => _owner.Main.CanShare;
    public bool CanOpenFolder => _owner.Main.CanOpenFolder;

    // ------------------------------------------------------------ status
    public bool IsRecording => Info.Status == TakeStatus.Recording;
    public bool IsRecovered => Info.Status == TakeStatus.Recovered;
    public bool IsSyncing => Info.Status == TakeStatus.Syncing;
    public bool HasStatus => IsRecording || IsRecovered || IsSyncing || Info.Status == TakeStatus.Pending;
    public bool IsIdle => !IsRecording && Info.Tracks.Count > 0;

    public string StatusText => Info.Status switch
    {
        TakeStatus.Recording => "Recording",
        TakeStatus.Recovered => "Recovered",
        TakeStatus.Syncing => "Syncing",
        TakeStatus.Pending => "Download pending",
        _ => "",
    };

    public string StatusDetail => Info.Status switch
    {
        TakeStatus.Recovered => "Recovered after the app closed unexpectedly",
        TakeStatus.Syncing => "Waiting for files from other devices",
        TakeStatus.Recording => "Still recording",
        _ => "",
    };

    // ------------------------------------------------------------ text
    public bool HasCustomTitle => !string.IsNullOrWhiteSpace(Info.Title);

    /// <summary>The user's title, or the date when there is none.</summary>
    public string Title => HasCustomTitle ? Info.Title! : DateText;

    public string DateText => FormatTime(Info.Time);
    public string DurationText => FormatDuration(Info.Duration);
    public string FormatText => Info.Format?.ShortName() ?? "";
    public string SizeText => FormatSize(Info.TotalSize);

    /// <summary>"Today, 14:32 · 3:21 · WAV · 24 MB" (the date is the title when the take has none).</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HasCustomTitle) { parts.Add(DateText); }
            parts.Add(DurationText);
            if (FormatText.Length > 0) { parts.Add(FormatText); }
            parts.Add(SizeText);
            return string.Join("  ·  ", parts);
        }
    }

    public IReadOnlyList<string> MicNames => Info.Tracks.Where(t => !t.IsVideo).Select(t => t.Name).Where(n => n.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public int DeviceCount => Math.Max(1, Info.Devices.Count);

    /// <summary>"Alice, Bob +2  ·  3 devices".</summary>
    public string MicsText
    {
        get
        {
            var names = MicNames;
            var parts = new List<string>();
            if (names.Count > 0)
            {
                parts.Add(names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" +{names.Count - 3}");
            }
            if (DeviceCount > 1) { parts.Add($"{DeviceCount} devices"); }
            return string.Join("  ·  ", parts);
        }
    }

    public bool HasMicsText => MicsText.Length > 0;
    public string Notes => Info.Notes ?? "";
    public bool HasNotes => !string.IsNullOrWhiteSpace(Info.Notes);
    public string NotesOrHint => HasNotes ? Info.Notes! : "No notes yet.";

    public IReadOnlyList<TrackLine> TrackLines => Info.Tracks.Select(t => new TrackLine(
        t.Name.Length > 0 ? t.Name : (t.IsVideo ? "Video" : "Audio"),
        string.Join("  ·  ", new[]
        {
            t.IsRemote ? t.Device! : TakeLibrary.ThisDevice,
            t.IsVideo ? "Video" : t.Codec.ToUpperInvariant(),
            FormatDuration(TimeSpan.FromMilliseconds(t.DurationMs)),
        }.Where(s => !string.IsNullOrEmpty(s))))).ToList();

    public string DeletedText => Info.DeletedAt is { } d
        ? "Deleted " + FormatAgo(DateTime.UtcNow - d.ToUniversalTime()) + "  ·  " + DurationText + "  ·  " + SizeText
        : "";

    /// <summary>Everything the search box looks at, lower case.</summary>
    public string SearchText => _searchText ??= string.Join('\n', new[]
    {
        Info.DisplayTitle, Info.Title ?? "", Info.Notes ?? "", string.Join(' ', MicNames), string.Join(' ', Info.Devices),
        Info.Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        Info.Time.ToString("d MMMM yyyy MMMM d dddd", CultureInfo.InvariantCulture),
        Info.Time.ToString("d MMM yyyy MMM d ddd", CultureInfo.CurrentCulture),
        Info.Time.ToString("D", CultureInfo.CurrentCulture),
        DateText,
    }).ToLowerInvariant();

    /// <summary>Takes a fresh copy of the library record (after rename, notes, recovery).</summary>
    public void Update(TakeInfo info)
    {
        _searchText = null;
        Info = info;
        UpdateRemoteDownloads();
        OnPropertyChanged(string.Empty);
    }

    // ------------------------------------------------------------ commands
    [RelayCommand]
    private Task ShareAsync() => _owner.ShareAsync([this]);

    [RelayCommand]
    private void OpenFolder() => _owner.OpenFolder(this);

    [RelayCommand]
    private void Open() => _owner.Open(this);

    [RelayCommand]
    private void ShowMenu() => _owner.ShowMenu(this);

    [RelayCommand]
    private void EditTitle() => _owner.BeginRename(this);

    [RelayCommand]
    private void EditNotes() => _owner.BeginNotes(this);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync([this]);

    [RelayCommand]
    private void ToggleChecked() => IsChecked = !IsChecked;

    [RelayCommand]
    private Task RestoreAsync() => _owner.RestoreAsync(this);

    [RelayCommand]
    private Task DeleteNowAsync() => _owner.DeleteNowAsync(this);

    // ------------------------------------------------------------ formatting
    public static string FormatTime(DateTime t)
    {
        var today = DateTime.Today;
        var day = t.Date == today ? "Today" : t.Date == today.AddDays(-1) ? "Yesterday"
            : t.ToString(t.Year == today.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.CurrentCulture);
        return $"{day}, {t.ToString("t", CultureInfo.CurrentCulture)}";
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : d.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };

    private static string FormatAgo(TimeSpan t) =>
        t.TotalMinutes < 1 ? "just now"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago"
        : t.TotalDays < 2 ? "yesterday"
        : $"{(int)t.TotalDays} days ago";

    // ------------------------------------------------------------ thumbnails (background)

    /// <summary>Loads the poster (decoded small) or picks the loudest audio track's peaks for the mini waveform.</summary>
    public async Task LoadThumbnailAsync()
    {
        var info = Info;
        if (info.PosterPath is { } poster)
        {
            var bmp = await Task.Run(() => LoadPoster(poster));
            if (bmp is not null)
            {
                Poster = bmp;
                return;
            }
        }
        var (peaks, scale) = await Task.Run(() => PickLoudest(info));
        if (peaks is null && info.Status != TakeStatus.Recording && info.Tracks.Any(t => !t.IsVideo))
        {
            // old take, remote files, AAC/MP3: generate the missing peaks in the background, then show the mini waveform
            var ok = await _owner.EnsurePeaksAsync(info);
            if (ok) { (peaks, scale) = await Task.Run(() => PickLoudest(info)); }
        }
        if (peaks is not null)
        {
            WaveScale = scale;
            WavePeaks = peaks;
        }
    }

    private static Bitmap? LoadPoster(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Bitmap.DecodeToWidth(fs, 240);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The audio track with the highest mean amplitude, and a gain that brings its peak close to full height.</summary>
    internal static (PeakReader? Peaks, double Scale) PickLoudest(TakeInfo info)
    {
        PeakReader? best = null;
        double bestMean = 0, bestPeak = 0;
        const int columns = 64;
        var min = new short[columns];
        var max = new short[columns];
        foreach (var t in info.Tracks.Where(t => !t.IsVideo))
        {
            var r = PeakReader.Open(TrackInfo.PeakPath(info.Folder, t.Id));
            if (r is null || r.SampleCount < columns) { continue; }
            r.GetRange(0, (double)r.SampleCount / columns, min, max);
            double sum = 0;
            int peak = 0;
            for (var i = 0; i < columns; i++)
            {
                sum += (max[i] - min[i]) / 65535.0;
                peak = Math.Max(peak, Math.Max(Math.Abs((int)max[i]), Math.Abs((int)min[i])));
            }
            var mean = sum / columns;
            if (best is null || mean > bestMean)
            {
                best = r;
                bestMean = mean;
                bestPeak = peak;
            }
        }
        var scale = best is null || bestPeak < 64 ? 1.0 : Math.Clamp(0.92 * 32768 / bestPeak, 1.0, 8.0);
        return (best, scale);
    }
}

public static class RecordingFormatNames
{
    public static string ShortName(this RecordingFormat f) => f switch
    {
        RecordingFormat.Wav => "WAV",
        RecordingFormat.M4a => "M4A",
        RecordingFormat.Mp3 => "MP3",
        RecordingFormat.Mka => "MKA",
        RecordingFormat.Mkv => "MKV",
        RecordingFormat.Mp4 => "MP4",
        _ => f.ToString().ToUpperInvariant(),
    };
}
