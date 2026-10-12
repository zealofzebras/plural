using Plural.Core.Media;

namespace Plural.Core.Library;

/// <summary>
/// Reads the tracks of a media file with the managed demuxers (<see cref="MediaReader"/>): kind, container track index, name, codec,
/// rate, channels, rotation, size and duration. Durations of crashed fragmented MP4 / Matroska files come from the readers' scan of
/// the samples or clusters, so they are real, not 0. Nothing is decoded. Throws when the file is not readable
/// (<see cref="TakeLibrary"/> then falls back to <see cref="TakeProbe.Basic"/>).
/// </summary>
public static class MediaProbe
{
    /// <summary>Tracks of <paramref name="file"/> (full path). <see cref="TrackInfo.File"/> and <see cref="TrackInfo.Id"/> are left for the caller.</summary>
    public static IReadOnlyList<TrackInfo> Probe(string file)
    {
        using var reader = MediaReader.Open(file);
        var list = new List<TrackInfo>();
        foreach (var t in reader.Tracks)
        {
            if (t.Kind is not (MediaKind.Audio or MediaKind.Video)) { continue; }
            var video = t.Kind == MediaKind.Video;
            var duration = t.Duration > TimeSpan.Zero ? t.Duration : reader.Duration;
            list.Add(new TrackInfo
            {
                Kind = video ? TrackKinds.Video : TrackKinds.Audio,
                TrackIndex = t.Index,
                Name = t.Name ?? "",
                Codec = t.Codec,
                SampleRate = video ? 0 : t.SampleRate,
                Channels = video ? 0 : t.Channels,
                Width = video ? t.Width : 0,
                Height = video ? t.Height : 0,
                Rotation = video ? t.RotationDegrees : 0,
                DurationMs = Math.Round(duration.TotalMilliseconds, 1),
            });
        }
        return list;
    }
}
