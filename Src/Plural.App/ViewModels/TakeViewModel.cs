using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using SmartMike.Core.Recording;

namespace SmartMike.App.ViewModels;

/// <summary>Facts about a take folder, gathered off the UI thread.</summary>
public sealed record TakeData(string Folder, DateTime Time, RecordingFormat Format, int Files, long Size, TimeSpan? Duration, int? Mics)
{
    public static List<TakeData> Scan(string root, IReadOnlyDictionary<string, (TimeSpan Duration, int Mics)> known, int max)
    {
        var result = new List<TakeData>();
        if (!Directory.Exists(root))
        {
            return result;
        }
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var files = Directory.GetFiles(dir).Where(f => !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToList();
                if (files.Count == 0)
                {
                    continue;
                }
                var format = FormatFromExtension(Path.GetExtension(files[0]));
                if (format is null)
                {
                    continue;
                }
                var name = Path.GetFileName(dir);
                var time = name.Length >= 15 && DateTime.TryParseExact(name[..15], "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                    ? t
                    : Directory.GetCreationTime(dir);
                var size = files.Sum(f => new FileInfo(f).Length);
                TimeSpan? duration = null;
                int? mics = null;
                if (known.TryGetValue(dir, out var k))
                {
                    duration = k.Duration;
                    mics = k.Mics;
                }
                result.Add(new TakeData(dir, time, format.Value, files.Count, size, duration, mics));
            }
            catch (IOException)
            {
                // folder vanished or is locked: skip
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return result.OrderByDescending(t => t.Time).Take(max).ToList();
    }

    private static RecordingFormat? FormatFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".wav" => RecordingFormat.Wav,
        ".m4a" => RecordingFormat.M4a,
        ".mp3" => RecordingFormat.Mp3,
        ".mka" => RecordingFormat.Mka,
        ".mkv" => RecordingFormat.Mkv,
        ".mp4" => RecordingFormat.Mp4,
        _ => null,
    };
}

/// <summary>One row of the recent takes list.</summary>
public partial class TakeViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;

    public string Folder { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool IsVideo { get; }
    public bool IsAudio => !IsVideo;

    public bool CanShare => _owner.CanShare;
    public bool CanOpenFolder => _owner.CanOpenFolder;

    public TakeViewModel(TakeData data, MainViewModel owner)
    {
        _owner = owner;
        Folder = data.Folder;
        IsVideo = data.Format.IsVideo();
        Title = FormatTime(data.Time);

        var parts = new List<string> { FormatLabel(data) };
        if (data.Duration is { } d)
        {
            parts.Add(FormatDuration(d));
        }
        parts.Add(FormatSize(data.Size));
        Subtitle = string.Join("  ·  ", parts);
    }

    private static string FormatLabel(TakeData d)
    {
        var name = d.Format.ShortName();
        var n = d.Format.IsMultiTrack() ? d.Mics : d.Files;
        if (n is null)
        {
            return name;
        }
        var unit = d.Format.IsMultiTrack() ? "track" : "file";
        return $"{name} · {n} {unit}{(n == 1 ? "" : "s")}";
    }

    private static string FormatTime(DateTime t)
    {
        var today = DateTime.Today;
        var day = t.Date == today ? "Today" : t.Date == today.AddDays(-1) ? "Yesterday" : t.ToString("MMM d", CultureInfo.CurrentCulture);
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

    [RelayCommand]
    private Task ShareAsync() => _owner.ShareTakeAsync(this);

    [RelayCommand]
    private void OpenFolder() => _owner.OpenTakeFolder(this);
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
