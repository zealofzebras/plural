using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plural.Core.Library;

/// <summary>Values of <see cref="TakeSidecar.Status"/> (and <see cref="TakeInfo.Status"/>).</summary>
public static class TakeStatus
{
    public const string Recording = "recording";
    public const string Complete = "complete";

    /// <summary>The app closed during the take; whatever was written is kept.</summary>
    public const string Recovered = "recovered";

    /// <summary>Only on <see cref="TakeInfo"/>: a remote device is still recording or sending its files.</summary>
    public const string Syncing = "syncing";

    public const string Pending = "pending";
}

public static class TrackKinds
{
    public const string Audio = "audio";
    public const string Video = "video";
}

/// <summary>One audio or video stream of a take (a file, or one track inside a multi-track file).</summary>
public sealed class TrackInfo
{
    /// <summary><c>&lt;file&gt;#&lt;trackIndex&gt;</c>, stable; used for peak files and playback settings.</summary>
    public string Id { get; set; } = "";

    /// <summary><see cref="TrackKinds.Audio"/> or <see cref="TrackKinds.Video"/>.</summary>
    public string Kind { get; set; } = TrackKinds.Audio;

    /// <summary>Path relative to the take folder, with <c>/</c> separators.</summary>
    public string File { get; set; } = "";

    /// <summary>Index inside the file (0 for single-track files; the container's track order for MKA/MKV/MP4).</summary>
    public int TrackIndex { get; set; }

    public string Name { get; set; } = "";

    /// <summary>null or "" = this device; otherwise the remote device's sub-folder name.</summary>
    public string? Device { get; set; }

    /// <summary>Where the track starts on the take's timeline (remote devices relative to the controller; the controller is 0).</summary>
    public double StartOffsetMs { get; set; }

    public double DurationMs { get; set; }

    /// <summary>"pcm_s16le", "flac", "aac", "mp3" or "h264".</summary>
    public string Codec { get; set; } = "";

    public int SampleRate { get; set; }
    public int Channels { get; set; }

    /// <summary>Clockwise degrees the stored frames need to be shown upright (video).</summary>
    public int Rotation { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsVideo => Kind == TrackKinds.Video;

    [JsonIgnore]
    public bool IsRemote => !string.IsNullOrEmpty(Device);

    public static string MakeId(string file, int trackIndex) => file.Replace('\\', '/') + "#" + trackIndex;

    /// <summary>File name (without folder) of the peaks file for a track id: <c>/</c>, <c>#</c>, <c>\</c> and invalid characters become <c>_</c>.</summary>
    public static string PeakFileName(string trackId)
    {
        return FileNames.Sanitize(trackId.Replace('#', '_'), "track") + ".peaks";
    }

    /// <summary>Full path of the peaks file of <paramref name="trackId"/> in <paramref name="takeFolder"/>.</summary>
    public static string PeakPath(string takeFolder, string trackId) => Path.Combine(takeFolder, TakeSidecar.PeaksFolder, PeakFileName(trackId));
}

/// <summary>A pause point (pause/resume removes the paused time from every track).</summary>
public sealed class PauseInfo
{
    /// <summary>Position of the pause in milliseconds on the take's output timeline (recorded time, paused time removed), i.e. where the cut sits in every track and in the video; the same for all tracks of this device.</summary>
    public double AtMs { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A stretch of a track that was filled with silence because the mic was away.</summary>
public sealed class DropoutInfo
{
    /// <summary><see cref="TrackInfo.Id"/> of the audio track.</summary>
    public string Track { get; set; } = "";

    /// <summary>Start in milliseconds on that track's own recorded timeline: from the track's sample 0, with paused time already removed (not take-timeline or wall time).</summary>
    public double StartMs { get; set; }

    /// <summary>End (exclusive) in the same track-local milliseconds.</summary>
    public double EndMs { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Player settings of one track, stored per track id.</summary>
public sealed class TrackPlayback
{
    public double GainDb { get; set; }
    public bool Mute { get; set; }
    public bool Solo { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsDefault => GainDb == 0 && !Mute && !Solo && Extra is not { Count: > 0 };
}

/// <summary>
/// <c>plural.json</c> in a take folder: what the app knows about the take that the media files do not say (title, notes, tracks,
/// pauses, dropouts, playback mix). Fields written by newer versions are kept when it is rewritten.
/// </summary>
public sealed class TakeSidecar
{
    public const string FileName = "plural.json";
    public const string PeaksFolder = ".peaks";
    public const string PosterFileName = "poster.png";
    private static readonly object Lock = new();

    public int Version { get; set; } = 1;

    /// <summary>null = show the timestamp.</summary>
    public string? Title { get; set; }

    public string? Notes { get; set; }
    public DateTime Created { get; set; }

    /// <summary>See <see cref="TakeStatus"/>.</summary>
    public string Status { get; set; } = TakeStatus.Complete;

    public List<TrackInfo> Tracks { get; set; } = [];
    public List<PauseInfo> Pauses { get; set; } = [];
    public List<DropoutInfo> Dropouts { get; set; } = [];

    /// <summary>Per track id.</summary>
    public Dictionary<string, TrackPlayback> Playback { get; set; } = [];

    /// <summary>Set (UTC) while the take is in the trash.</summary>
    public DateTime? DeletedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Reads the sidecar; null when it is missing or corrupt. Throws <see cref="IOException"/> when it exists but cannot be read (after a short retry).</summary>
    public static TakeSidecar? Load(string folder)
    {
        lock (Lock)
        {
            return LoadCore(folder, out _);
        }
    }

    private enum ReadState { Ok, Missing, Corrupt }

    private static TakeSidecar? LoadCore(string folder, out ReadState state)
    {
        var path = Path.Combine(folder, FileName);
        string text;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) { state = ReadState.Missing; return null; }
                text = File.ReadAllText(path);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // an antivirus scanner or sync client may hold the file for a moment; never treat that as "no sidecar"
                if (ex is FileNotFoundException or DirectoryNotFoundException) { state = ReadState.Missing; return null; }
                if (attempt >= 4) { throw ex as IOException ?? new IOException(ex.Message, ex); }
                Thread.Sleep(40 * (attempt + 1));
            }
        }
        try
        {
            var s = JsonSerializer.Deserialize(text, LibraryJson.Default.TakeSidecar);
            if (s is null) { state = ReadState.Corrupt; return null; }
            s.Tracks ??= [];
            s.Pauses ??= [];
            s.Dropouts ??= [];
            s.Playback ??= [];
            s.Status ??= TakeStatus.Complete;
            state = ReadState.Ok;
            return s;
        }
        catch (JsonException)
        {
            state = ReadState.Corrupt;
            return null;
        }
    }

    /// <summary>
    /// Writes <paramref name="sidecar"/> atomically (<c>.tmp</c> + rename); safe from several threads. The take folder must exist
    /// (a deleted or moved take is not brought back by a late write). A corrupt file it replaces is kept as <c>plural.json.bak</c>.
    /// </summary>
    public static void Save(string folder, TakeSidecar sidecar)
    {
        lock (Lock)
        {
            if (!Directory.Exists(folder)) { throw new DirectoryNotFoundException(folder); }
            LoadCore(folder, out var state);
            if (state == ReadState.Corrupt) { Backup(folder); }
            SaveCore(folder, sidecar);
        }
    }

    private static void Backup(string folder)
    {
        try { File.Copy(Path.Combine(folder, FileName), Path.Combine(folder, FileName + ".bak"), overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static void SaveCore(string folder, TakeSidecar sidecar, bool durable = true)
    {
        var path = Path.Combine(folder, FileName);
        var tmp = path + ".tmp";
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sidecar, LibraryJson.Default.TakeSidecar));
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            if (durable) { fs.Flush(true); } // data on disk before the rename, so a power loss cannot leave an empty plural.json
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Reads (or creates when it is missing; a corrupt file is replaced after keeping a <c>.bak</c>), changes and writes the sidecar
    /// atomically. A sidecar that exists but cannot be read throws <see cref="IOException"/> instead of being replaced.
    /// The take folder must exist unless <paramref name="createFolder"/> (only the recorder, when it starts a take).
    /// <paramref name="durable"/> false skips the fsync before the rename (the recorder's first write must not delay the start of the capture;
    /// a torn file after a power loss is then rebuilt like a corrupt one).
    /// </summary>
    public static TakeSidecar Update(string folder, Action<TakeSidecar> change, bool createFolder = false, bool durable = true)
    {
        lock (Lock)
        {
            if (createFolder) { Directory.CreateDirectory(folder); }
            else if (!Directory.Exists(folder)) { throw new DirectoryNotFoundException(folder); }
            var s = LoadCore(folder, out var state);
            if (state == ReadState.Corrupt) { Backup(folder); }
            s ??= new TakeSidecar { Created = DateTime.Now };
            change(s);
            SaveCore(folder, s, durable);
            return s;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TakeSidecar))]
internal sealed partial class LibraryJson : JsonSerializerContext;
