using Plural.Core.Recording;
using Plural.Core.Remote;

namespace Plural.Core.Library;

/// <summary>
/// The takes in the recording root: scan, read, rename/annotate, soft-delete (<c>.trash</c>) and recover. Every take folder keeps its
/// metadata in <c>plural.json</c> (<see cref="TakeSidecar"/>); folders from before that get one on first read. Safe to call from any
/// thread; do not call from the UI thread for a long list (reads files).
/// </summary>
public sealed class TakeLibrary
{
    public const string TrashFolderName = ".trash";

    /// <summary>Where titled share zips live (<c>&lt;root&gt;/.share/&lt;take&gt;/&lt;title&gt;.zip</c>).</summary>
    public const string ShareFolderName = ".share";

    /// <summary>Device name of a take recorded without remote mode.</summary>
    public const string ThisDevice = "This device";

    private readonly Func<string, IReadOnlyList<TrackInfo>>? _probe;

    /// <param name="root">The recording root; every sub-folder not starting with '.' is a candidate take.</param>
    /// <param name="probe">
    /// Reads the tracks of a media file (full path) for takes without a sidecar and for files received from remote devices; may throw or
    /// return nothing, then <see cref="TakeProbe.Basic"/> is used. Default: <see cref="MediaProbe.Probe"/>. The caller fills in
    /// <see cref="TrackInfo.File"/>, id and device.
    /// </param>
    public TakeLibrary(string root, Func<string, IReadOnlyList<TrackInfo>>? probe = null)
    {
        Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _probe = probe ?? MediaProbe.Probe;
    }

    public string Root { get; }

    public string TrashRoot => Path.Combine(Root, TrashFolderName);

    // ------------------------------------------------------------------ reading

    /// <summary>All takes, newest first. A broken folder is skipped; never throws for one bad folder.</summary>
    public IReadOnlyList<TakeInfo> Scan() => ScanFolder(Root);

    private List<TakeInfo> ScanFolder(string dir)
    {
        var result = new List<TakeInfo>();
        if (!Directory.Exists(dir)) { return result; }
        IEnumerable<string> dirs;
        try { dirs = Directory.GetDirectories(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return result; }
        foreach (var d in dirs)
        {
            if (Path.GetFileName(d).StartsWith('.')) { continue; }
            try
            {
                var t = Load(d);
                if (t is not null) { result.Add(t); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // folder vanished, is locked or is damaged (a parser may throw anything on hostile bytes): skip it
            }
        }
        result.Sort((a, b) =>
        {
            var c = b.Time.CompareTo(a.Time);
            return c != 0 ? c : string.Compare(b.Id, a.Id, StringComparison.Ordinal);
        });
        return result;
    }

    /// <summary>Reads one take (creating its sidecar if it has none); null if the folder holds no recording.</summary>
    public TakeInfo? Load(string folder)
    {
        folder = Normalize(folder);
        if (!Directory.Exists(folder)) { return null; }
        var sc = TakeSidecar.Load(folder);
        var dirty = false;
        if (sc is null)
        {
            sc = BuildSidecar(folder);
            dirty = true;
        }
        var manifest = TakeManifest.Load(folder);
        var transient = new List<TrackInfo>();
        dirty |= MergeRemote(folder, sc, manifest, transient);
        if (sc.Tracks.Count + transient.Count == 0 && manifest?.Devices.Any(d => d.Role == "remote") != true) { return null; }
        if (dirty)
        {
            try { TakeSidecar.Save(folder, sc); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* read-only media: still usable */ }
        }
        return BuildInfo(folder, sc, manifest, transient);
    }

    private static string Normalize(string folder) =>
        Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private TakeSidecar BuildSidecar(string folder)
    {
        var name = Path.GetFileName(folder);
        var sc = new TakeSidecar
        {
            Created = TakeInfo.ParseFolderTime(name) ?? Directory.GetCreationTime(folder),
            Status = TakeStatus.Complete,
        };
        foreach (var f in MediaFiles(folder)) { sc.Tracks.AddRange(ProbeFile(folder, f, null)); }
        return sc;
    }

    private static List<string> MediaFiles(string dir) =>
        Directory.GetFiles(dir).Where(TakeProbe.IsMedia).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    private List<TrackInfo> ProbeFile(string takeFolder, string file, string? device)
    {
        IReadOnlyList<TrackInfo> found = [];
        if (_probe is not null)
        {
            try { found = _probe(file); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { found = []; }
        }
        if (found.Count == 0) { found = TakeProbe.Basic(file); }
        var rel = Path.GetRelativePath(takeFolder, file).Replace('\\', '/');
        var baseName = Path.GetFileNameWithoutExtension(file);
        if (found.Count == 0)
        {
            // unreadable: keep the file in the take so it can still be shared
            var video = TakeProbe.FormatFromExtension(file) is RecordingFormat.Mkv or RecordingFormat.Mp4;
            found = [new TrackInfo
            {
                Kind = video ? TrackKinds.Video : TrackKinds.Audio,
                Codec = video ? "h264" : Path.GetExtension(file).ToLowerInvariant() switch { ".wav" => "pcm_s16le", ".mp3" => "mp3", ".mka" => "flac", _ => "aac" },
            }];
        }
        var list = found.ToList();
        var distinct = list.Select(t => t.TrackIndex).Distinct().Count() == list.Count;
        for (var i = 0; i < list.Count; i++)
        {
            var t = list[i];
            if (!distinct) { t.TrackIndex = i; }
            t.File = rel;
            t.Id = TrackInfo.MakeId(rel, t.TrackIndex);
            t.Device = device;
            if (string.IsNullOrWhiteSpace(t.Name)) { t.Name = t.IsVideo ? "Video" : baseName; }
        }
        return list;
    }

    /// <summary>
    /// Adds the tracks of remote devices (sub-folders) and sets their offsets from <c>take.json</c>. A device's
    /// <c>startOffsetMs</c> is its first sample versus the scheduled start; a track's offset is that minus the controller's own,
    /// so the controller's tracks stay at 0 and a device that started later is shifted right. Tracks of devices that are still
    /// recording or syncing are returned in <paramref name="transient"/> and not stored.
    /// </summary>
    private bool MergeRemote(string folder, TakeSidecar sc, TakeManifest? manifest, List<TrackInfo> transient)
    {
        var dirty = false;
        var controllerOffset = manifest?.Devices.FirstOrDefault(d => d.Role == "controller")?.StartOffsetMs ?? 0;
        var devices = new List<(string Folder, double? Offset, bool Final)>();
        if (manifest is not null)
        {
            foreach (var d in manifest.Devices.Where(d => !string.IsNullOrEmpty(d.Folder)))
            {
                devices.Add((d.Folder, d.StartOffsetMs, d.Status == "synced"));
            }
        }
        foreach (var sub in Directory.GetDirectories(folder))
        {
            var n = Path.GetFileName(sub);
            if (n.StartsWith('.') || devices.Any(d => string.Equals(d.Folder, n, StringComparison.OrdinalIgnoreCase))) { continue; }
            devices.Add((n, null, true));
        }
        foreach (var (name, offset, final) in devices)
        {
            var dir = Path.Combine(folder, name);
            if (!Directory.Exists(dir)) { continue; }
            var shift = offset is { } o ? o - controllerOffset : 0;
            var existing = sc.Tracks.Where(t => t.Device == name).ToList();
            if (existing.Count > 0)
            {
                foreach (var t in existing.Where(t => t.StartOffsetMs != shift))
                {
                    t.StartOffsetMs = shift;
                    dirty = true;
                }
                continue;
            }
            var added = new List<TrackInfo>();
            foreach (var f in MediaFiles(dir))
            {
                foreach (var t in ProbeFile(folder, f, name))
                {
                    t.StartOffsetMs = shift;
                    added.Add(t);
                }
            }
            if (added.Count == 0) { continue; }
            if (final)
            {
                sc.Tracks.AddRange(added);
                dirty = true;
            }
            else
            {
                transient.AddRange(added);
            }
        }
        return dirty;
    }

    private static TakeInfo BuildInfo(string folder, TakeSidecar sc, TakeManifest? manifest, List<TrackInfo> transient)
    {
        var tracks = sc.Tracks.Concat(transient).ToList();
        var name = Path.GetFileName(folder);
        double end = 0, start = 0;
        foreach (var t in tracks)
        {
            end = Math.Max(end, t.StartOffsetMs + t.DurationMs);
            start = Math.Min(start, t.StartOffsetMs);
        }
        long size = 0;
        foreach (var f in tracks.Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { size += new FileInfo(Path.Combine(folder, f)).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* missing file */ }
        }
        // this device first (take.json does not list the controller itself today), then the remote devices
        var self = manifest?.Devices.FirstOrDefault(d => d.Role == "controller")?.Name;
        if (string.IsNullOrWhiteSpace(self)) { self = manifest?.Controller; }
        var devices = new List<string> { string.IsNullOrWhiteSpace(self) ? ThisDevice : self };
        if (manifest is not null)
        {
            devices.AddRange(manifest.Devices.Where(d => d.Role != "controller").Select(d => d.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
        }
        foreach (var d in tracks.Where(t => t.IsRemote).Select(t => t.Device!).Distinct())
        {
            if (!devices.Contains(d, StringComparer.OrdinalIgnoreCase)) { devices.Add(d); }
        }
        var status = sc.Status;
        if (status != TakeStatus.Recording && manifest?.Devices.Any(d => d.Role == "remote" && d.Status is "pending" or "failed") == true)
        {
            status = TakeStatus.Pending;
        }
        if (status != TakeStatus.Recording && manifest is not null
            && manifest.Devices.Any(d => d.Role != "controller" && d.Status is "recording" or "syncing"))
        {
            status = TakeStatus.Syncing;
        }
        var poster = Path.Combine(folder, TakeSidecar.PosterFileName);
        return new TakeInfo
        {
            Folder = folder,
            Title = string.IsNullOrWhiteSpace(sc.Title) ? null : sc.Title,
            Notes = sc.Notes,
            Created = sc.Created,
            Time = TakeInfo.ParseFolderTime(name) ?? sc.Created,
            Duration = TimeSpan.FromMilliseconds(end - start),
            Format = tracks.Count > 0 ? TakeProbe.FormatFromExtension(tracks[0].File) : null,
            TotalSize = size,
            Devices = devices,
            Tracks = tracks,
            PosterPath = File.Exists(poster) ? poster : null,
            Status = status,
            IsRemote = tracks.Any(t => t.IsRemote) || manifest?.Devices.Any(d => d.Role == "remote") == true,
            HasVideo = tracks.Any(t => t.IsVideo),
            Pauses = sc.Pauses,
            Dropouts = sc.Dropouts,
            Playback = sc.Playback,
            DeletedAt = sc.DeletedAt,
        };
    }

    // ------------------------------------------------------------------ editing

    /// <summary>Sets the title (null or empty = show the timestamp again). The folder name stays.</summary>
    public TakeInfo? Rename(string folder, string? title)
    {
        var t = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        return Edit(folder, s => s.Title = t);
    }

    public TakeInfo? SetNotes(string folder, string? notes) =>
        Edit(folder, s => s.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes);

    /// <summary>Changes the player settings of one track; null arguments are left as they are. All-default settings are removed.</summary>
    public TakeInfo? SetPlayback(string folder, string trackId, double? gainDb = null, bool? mute = null, bool? solo = null) =>
        Edit(folder, s =>
        {
            if (!s.Playback.TryGetValue(trackId, out var p)) { p = new TrackPlayback(); }
            if (gainDb is { } g) { p.GainDb = g; }
            if (mute is { } m) { p.Mute = m; }
            if (solo is { } so) { p.Solo = so; }
            if (p.IsDefault) { s.Playback.Remove(trackId); }
            else { s.Playback[trackId] = p; }
        });

    private TakeInfo? Edit(string folder, Action<TakeSidecar> change)
    {
        folder = Normalize(folder);
        Load(folder); // makes sure a sidecar with the probed tracks exists
        TakeSidecar.Update(folder, change);
        return Load(folder);
    }

    // ------------------------------------------------------------------ trash

    private string RequireTake(string folder)
    {
        folder = Normalize(folder);
        if (!string.Equals(Path.GetDirectoryName(folder), Root, PathComparison) || Path.GetFileName(folder).StartsWith('.'))
        {
            throw new ArgumentException("Not a take folder of this library.", nameof(folder));
        }
        if (!Directory.Exists(folder)) { throw new DirectoryNotFoundException(folder); }
        return folder;
    }

    private string RequireTrashed(string folder)
    {
        folder = Normalize(folder);
        if (!string.Equals(Path.GetDirectoryName(folder), TrashRoot, PathComparison))
        {
            throw new ArgumentException("Not a folder in the trash of this library.", nameof(folder));
        }
        if (!Directory.Exists(folder)) { throw new DirectoryNotFoundException(folder); }
        return folder;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string UniqueFolder(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        for (var i = 2; Directory.Exists(path) || File.Exists(path); i++) { path = Path.Combine(parent, $"{name}_{i}"); }
        return path;
    }

    /// <summary>
    /// Soft delete: moves the take to <c>.trash</c> with a deletion time and removes its stale share files. Returns the new folder.
    /// Refuses (<see cref="InvalidOperationException"/>) a take that is being recorded: <paramref name="activeFolder"/> (the take the
    /// app is recording now) or a sidecar still saying <c>recording</c> (a recovered take is fine). The folder is moved first and
    /// <c>deletedAt</c> written in the new place, so nothing can recreate the old folder; the move is undone if that fails.
    /// </summary>
    public string Delete(string folder, string? activeFolder = null)
    {
        folder = RequireTake(folder);
        var name = Path.GetFileName(folder);
        if (activeFolder is not null && string.Equals(Normalize(activeFolder), folder, PathComparison))
        {
            throw new InvalidOperationException("The take is being recorded.");
        }
        Load(folder); // sidecar for old takes, so Restore brings back the same tracks
        if (TakeSidecar.Load(folder)?.Status == TakeStatus.Recording)
        {
            throw new InvalidOperationException("The take is being recorded.");
        }
        Directory.CreateDirectory(TrashRoot);
        var target = UniqueFolder(TrashRoot, name);
        Directory.Move(folder, target);
        try
        {
            TakeSidecar.Update(target, s => s.DeletedAt = DateTime.UtcNow);
        }
        catch
        {
            try { Directory.Move(target, folder); } catch { /* leave it in the trash rather than lose track of it */ }
            throw;
        }
        DeleteShareFiles(name);
        return target;
    }

    private void DeleteShareFiles(string takeName)
    {
        try
        {
            var zip = Path.Combine(Root, takeName + ".zip");
            if (File.Exists(zip)) { File.Delete(zip); }
            var share = Path.Combine(Root, ShareFolderName, takeName);
            if (Directory.Exists(share)) { Directory.Delete(share, true); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a share file in use is not worth failing the delete
        }
    }

    /// <summary>Moves a take back out of the trash. Returns the restored folder.</summary>
    public string Restore(string trashFolder)
    {
        trashFolder = RequireTrashed(trashFolder);
        var target = UniqueFolder(Root, Path.GetFileName(trashFolder));
        Directory.Move(trashFolder, target);
        try
        {
            TakeSidecar.Update(target, s => s.DeletedAt = null);
        }
        catch
        {
            try { Directory.Move(target, trashFolder); } catch { /* keep the take rather than lose it */ }
            throw;
        }
        return target;
    }

    /// <summary>The trashed takes, most recently deleted first.</summary>
    public IReadOnlyList<TakeInfo> ListTrash()
    {
        var list = ScanTrash();
        return list.OrderByDescending(t => t.DeletedAt ?? DateTime.MinValue).ToList();
    }

    private List<TakeInfo> ScanTrash()
    {
        var result = new List<TakeInfo>();
        if (!Directory.Exists(TrashRoot)) { return result; }
        foreach (var d in Directory.GetDirectories(TrashRoot))
        {
            try
            {
                var t = Load(d);
                if (t is not null) { result.Add(t with { DeletedAt = t.DeletedAt ?? Directory.GetLastWriteTimeUtc(d) }); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // skip
            }
        }
        return result;
    }

    /// <summary>Deletes a trashed take for good.</summary>
    public void DeleteNow(string trashFolder)
    {
        trashFolder = RequireTrashed(trashFolder);
        Directory.Delete(trashFolder, true);
    }

    /// <summary>Deletes trashed takes deleted more than <paramref name="maxAge"/> ago (directory time if the sidecar has no <c>deletedAt</c>). Returns how many.</summary>
    public int PurgeTrash(TimeSpan maxAge, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(TrashRoot)) { return 0; }
        var now = nowUtc ?? DateTime.UtcNow;
        var purged = 0;
        foreach (var d in Directory.GetDirectories(TrashRoot))
        {
            try
            {
                var deleted = TakeSidecar.Load(d)?.DeletedAt ?? Directory.GetLastWriteTimeUtc(d);
                if (now - deleted.ToUniversalTime() > maxAge)
                {
                    Directory.Delete(d, true);
                    purged++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try again next start
            }
        }
        return purged;
    }

    // ------------------------------------------------------------------ recovery

    /// <summary>
    /// Takes whose sidecar still says <c>recording</c> were interrupted (crash, kill): marks them <c>recovered</c>, repairs the headers
    /// of their WAV and Matroska files in place (<see cref="MediaRepair"/>), and fills in the track durations from the files (probe;
    /// a track the probe cannot read keeps its peaks file's length). Call at app start, off the UI thread (it reads the files);
    /// <paramref name="exceptFolder"/> is a take being recorded now. Returns the recovered folders.
    /// </summary>
    public IReadOnlyList<string> RecoverInterrupted(string? exceptFolder = null)
    {
        var result = new List<string>();
        if (!Directory.Exists(Root)) { return result; }
        var except = exceptFolder is null ? null : Normalize(exceptFolder);
        foreach (var d in Directory.GetDirectories(Root))
        {
            if (Path.GetFileName(d).StartsWith('.') || (except is not null && string.Equals(Normalize(d), except, PathComparison))) { continue; }
            try
            {
                var loaded = TakeSidecar.Load(d);
                if (loaded?.Status != TakeStatus.Recording) { continue; }
                var durations = RepairAndMeasure(d, loaded.Tracks); // file work outside the sidecar lock
                TakeSidecar.Update(d, s =>
                {
                    if (s.Status != TakeStatus.Recording) { return; }
                    s.Status = TakeStatus.Recovered;
                    foreach (var t in s.Tracks.Where(t => t.DurationMs <= 0))
                    {
                        if (durations.TryGetValue(t.Id, out var ms) && ms > 0) { t.DurationMs = ms; }
                    }
                });
                result.Add(d);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // skip
            }
        }
        return result;
    }

    /// <summary>Repairs each distinct file of the tracks, then measures every track (probe, else the peaks file). Keyed by track id.</summary>
    private Dictionary<string, double> RepairAndMeasure(string folder, IReadOnlyList<TrackInfo> tracks)
    {
        var result = new Dictionary<string, double>();
        foreach (var group in tracks.Where(t => !string.IsNullOrEmpty(t.File)).GroupBy(t => t.File, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, group.Key);
            if (!File.Exists(path)) { continue; }
            MediaRepair.TryRepair(path);
            IReadOnlyList<TrackInfo> found = [];
            try { found = (_probe ?? MediaProbe.Probe)(path); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* fall back below */ }
            if (found.Count == 0) { found = TakeProbe.Basic(path); }
            foreach (var t in group)
            {
                var match = found.FirstOrDefault(f => f.TrackIndex == t.TrackIndex);
                if (match is { DurationMs: > 0 }) { result[t.Id] = match.DurationMs; }
                else if (!t.IsVideo && PeakReader.Open(TrackInfo.PeakPath(folder, t.Id)) is { } peaks)
                {
                    result[t.Id] = peaks.Duration.TotalMilliseconds;
                }
            }
        }
        return result;
    }
}
