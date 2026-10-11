using System.Text.Json;

namespace Plural.Core.Remote;

/// <summary>
/// <c>take.json</c> in a take folder that collected files from other devices: who recorded what, and where each device's
/// recording starts relative to the scheduled start (clock-synced), so the package can be lined up in an editor.
/// </summary>
public sealed class TakeManifest
{
    public const string FileName = "take.json";
    private static readonly object Lock = new();

    public int Version { get; set; } = 1;
    public string TakeId { get; set; } = "";
    public DateTime Created { get; set; }
    public string Controller { get; set; } = "";
    public string Format { get; set; } = "";
    public List<ManifestDevice> Devices { get; set; } = [];

    public static TakeManifest? Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        lock (Lock)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), RemoteJson.Default.TakeManifest) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Reads (or creates), changes and writes the manifest atomically; safe from several threads.</summary>
    public static void Update(string folder, Action<TakeManifest> change)
    {
        lock (Lock)
        {
            var m = Load(folder) ?? new TakeManifest { Created = DateTime.Now };
            change(m);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, RemoteJson.Default.TakeManifest));
            File.Move(tmp, path, overwrite: true);
        }
    }

    public ManifestDevice Device(string id, string name)
    {
        var d = Devices.FirstOrDefault(x => x.Id == id);
        if (d is null)
        {
            d = new ManifestDevice { Id = id, Name = name };
            Devices.Add(d);
        }
        return d;
    }
}

public sealed class ManifestDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"controller" or "remote".</summary>
    public string Role { get; set; } = "remote";

    /// <summary>Sub-folder holding this device's files ("" = the take folder itself).</summary>
    public string Folder { get; set; } = "";

    public List<string> Files { get; set; } = [];

    /// <summary>Where this device's recording starts, in ms after the scheduled start (null if unknown).</summary>
    public double? StartOffsetMs { get; set; }

    /// <summary>Half the best clock-sync round trip: the offset is good to about this many ms.</summary>
    public double? ClockUncertaintyMs { get; set; }

    public double Seconds { get; set; }

    /// <summary>"recording", "syncing", "synced", "failed".</summary>
    public string Status { get; set; } = "recording";

    public List<string> Errors { get; set; } = [];
}
