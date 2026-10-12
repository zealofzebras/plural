using System.Globalization;
using Plural.Core.Recording;

namespace Plural.Core.Library;

/// <summary>Everything the Takes page and the player need to know about one take folder.</summary>
public sealed record TakeInfo
{
    /// <summary>Full path of the take folder (the stable id).</summary>
    public required string Folder { get; init; }

    /// <summary>Folder name.</summary>
    public string Id => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>The user's title; null = show the timestamp.</summary>
    public string? Title { get; init; }

    public string? Notes { get; init; }
    public DateTime Created { get; init; }

    /// <summary>Start time: parsed from the folder name (<c>yyyyMMdd_HHmmss</c>), else <see cref="Created"/>.</summary>
    public DateTime Time { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>From the extension of the first media file; null if the take has none.</summary>
    public RecordingFormat? Format { get; init; }

    /// <summary>Size of the media files (not the sidecar, peaks or poster).</summary>
    public long TotalSize { get; init; }

    /// <summary>This device (or the controller) and the remote devices.</summary>
    public IReadOnlyList<string> Devices { get; init; } = [];

    public IReadOnlyList<TrackInfo> Tracks { get; init; } = [];
    public string? PosterPath { get; init; }

    /// <summary>See <see cref="TakeStatus"/>.</summary>
    public string Status { get; init; } = TakeStatus.Complete;

    public bool IsRemote { get; init; }
    public bool HasVideo { get; init; }
    public IReadOnlyList<PauseInfo> Pauses { get; init; } = [];
    public IReadOnlyList<DropoutInfo> Dropouts { get; init; } = [];
    public IReadOnlyDictionary<string, TrackPlayback> Playback { get; init; } = new Dictionary<string, TrackPlayback>();
    public DateTime? DeletedAt { get; init; }

    /// <summary>The title, or the formatted timestamp.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FormatTime(Time) : Title;

    public static string FormatTime(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Timestamp in a take folder name (<c>yyyyMMdd_HHmmss</c> plus an optional suffix), or null.</summary>
    public static DateTime? ParseFolderTime(string folderName) =>
        folderName.Length >= 15 && DateTime.TryParseExact(folderName[..15], "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;
}
