using Plural.Core.Audio;
using Plural.Core.Library;
using Plural.Core.Platform;
using Plural.Core.Session;

namespace Plural.Core.Recording;

/// <summary>Result of a finished recording.</summary>
public sealed record RecordedTake(string Folder, RecordingFormat Format, IReadOnlyList<string> Files, TimeSpan Duration)
{
    /// <summary>Non-fatal problems on the writer thread (empty when everything went fine).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Queue items dropped because the writer could not keep up (0 normally).</summary>
    public long DroppedItems { get; init; }

    /// <summary>The tracks of the take as stored in the sidecar (ids, files, durations); empty if the sidecar could not be written.</summary>
    public IReadOnlyList<TrackInfo> Tracks { get; init; } = [];

    /// <summary>Full path of <c>plural.json</c> (null if it could not be written).</summary>
    public string? SidecarPath { get; init; }
}

/// <summary>Zip/share helper for finished takes.</summary>
public static class TakeExport
{
    /// <summary>True for the library's own files (sidecar, poster, peaks), which are never shared.</summary>
    internal static bool IsLibraryFile(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/');
        return rel.Equals(TakeSidecar.FileName, StringComparison.OrdinalIgnoreCase)
            || rel.Equals(TakeSidecar.PosterFileName, StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(TakeSidecar.PeaksFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>File-name-safe version of a title (invalid characters become '_'); empty if nothing is left.</summary>
    public static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) { return ""; }
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(title.Select(c => bad.Contains(c) || c == '…' ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        return s.Length > 100 ? s[..100].TrimEnd('.', ' ') : s;
    }

    /// <summary>
    /// The single media file of the take, or (for several files) a zip, created or reused when it is newer than every file in the
    /// take. Without a title the zip is <c>&lt;takeFolder&gt;.zip</c> next to the folder; with a title it is
    /// <c>&lt;root&gt;/.share/&lt;take&gt;/&lt;sanitized title&gt;.zip</c>. The sidecar, poster and peaks are not shared.
    /// </summary>
    public static string GetShareFile(string takeFolder, string? title = null)
    {
        var folder = takeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Takes with remote devices hold one sub-folder per device and take.json: the zip keeps that layout.
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !IsLibraryFile(Path.GetRelativePath(folder, f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            throw new FileNotFoundException("The take folder contains no recordings.", folder);
        }
        if (files.Count == 1)
        {
            return files[0];
        }
        var zip = folder + ".zip";
        var name = SanitizeTitle(title);
        if (name.Length > 0)
        {
            var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(folder))!, TakeLibrary.ShareFolderName, Path.GetFileName(folder));
            Directory.CreateDirectory(dir);
            zip = Path.Combine(dir, name + ".zip");
        }
        if (File.Exists(zip) && File.GetLastWriteTimeUtc(zip) >= files.Max(File.GetLastWriteTimeUtc))
        {
            return zip;
        }
        var tmp = zip + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var za = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var f in files)
            {
                var e = za.CreateEntry(Path.GetRelativePath(folder, f).Replace('\\', '/'), System.IO.Compression.CompressionLevel.Fastest);
                using var es = e.Open();
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read);
                src.CopyTo(es);
            }
        }
        File.Move(tmp, zip, overwrite: true);
        return zip;
    }
}

internal interface IMicSink
{
    void Write(short[] pcm);
    void Complete();
}

/// <summary>Writer-thread output of a take (files, containers).</summary>
internal abstract class TakeOutput
{
    public List<string> Files { get; } = [];
    public IMicSink[] Mics { get; protected set; } = [];
    public virtual void Video(byte[] annexB, long timestamp, bool keyFrame) { }

    /// <summary>True (valid after <see cref="Complete"/>) when a video track was configured but no video ended up in the file.</summary>
    public virtual bool VideoDropped => false;

    public abstract void Complete();
}

internal sealed class WavSink(string path) : IMicSink
{
    private readonly WavWriter _w = new(path, ClockLockedTrack.OutputRate, 1);
    public void Write(short[] pcm) => _w.Write(pcm);
    public void Complete() => _w.Dispose();
}

internal sealed class Mp3Sink : IMicSink
{
    private readonly FileStream _fs;
    private readonly LameMp3Encoder _enc;

    public Mp3Sink(string path, int bitRate)
    {
        _enc = new LameMp3Encoder(ClockLockedTrack.OutputRate, bitRate);
        _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
    }

    public void Write(short[] pcm) => _enc.Encode(pcm, _fs);

    public void Complete()
    {
        try { _enc.Finish(_fs); }
        finally { _fs.Dispose(); _enc.Dispose(); }
    }
}

internal sealed class M4aSink : IMicSink
{
    private readonly Mp4Writer _mp4;
    private readonly Mp4Writer.Track _track;
    private readonly IAacEncoder _enc;

    public M4aSink(string path, string name, IAacEncoder enc)
    {
        _enc = enc;
        _mp4 = new Mp4Writer(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16), audioOnly: true);
        _track = _mp4.AddAudio(name, ClockLockedTrack.OutputRate, 1, enc.AudioSpecificConfig.ToArray());
        _track.PrimingSamples = enc.PrimingSamples;
    }

    public void Write(short[] pcm) => _enc.Encode(pcm, s => _mp4.WriteSample(_track, 0, s.Data.Span));

    public void Complete()
    {
        try
        {
            _enc.Flush(s => _mp4.WriteSample(_track, 0, s.Data.Span));
            if (_enc.AudioSpecificConfig.Length > 0) { _track.AudioSpecificConfig = _enc.AudioSpecificConfig.ToArray(); }
        }
        finally { _mp4.Dispose(); _enc.Dispose(); }
    }
}

internal sealed class PerMicOutput : TakeOutput
{
    public PerMicOutput(IMicSink[] sinks, IEnumerable<string> files)
    {
        Mics = sinks;
        Files.AddRange(files);
    }

    public override void Complete()
    {
        Exception? first = null;
        foreach (var m in Mics)
        {
            try { m.Complete(); }
            catch (Exception ex) { first ??= ex; }
        }
        if (first is not null) { throw first; }
    }
}

/// <summary>MKA / MKV: FLAC or PCM tracks, optional H.264.</summary>
internal sealed class MatroskaOutput : TakeOutput
{
    private readonly MatroskaWriter _mkv;
    private readonly MatroskaWriter.Track? _video;
    private bool _gotKey;

    private sealed class MkSink : IMicSink
    {
        private readonly MatroskaWriter _w;
        private readonly MatroskaWriter.Track _t;
        private readonly FlacEncoder? _flac;
        private readonly short[] _pcmBlock = new short[FlacEncoder.BlockSize];
        private int _n;
        private long _samples;

        public MkSink(MatroskaWriter w, MatroskaWriter.Track t, FlacEncoder? flac) { _w = w; _t = t; _flac = flac; }

        public void Write(short[] pcm)
        {
            if (_flac is not null)
            {
                _flac.Encode(pcm, EmitFlac);
                return;
            }
            var src = pcm.AsSpan();
            while (!src.IsEmpty)
            {
                var k = Math.Min(src.Length, _pcmBlock.Length - _n);
                src[..k].CopyTo(_pcmBlock.AsSpan(_n));
                _n += k;
                src = src[k..];
                if (_n == _pcmBlock.Length) { EmitPcm(); }
            }
        }

        private void EmitFlac(ReadOnlySpan<byte> frame, int count)
        {
            _w.WriteBlock(_t, _samples * 10_000_000 / ClockLockedTrack.OutputRate, frame);
            _samples += count;
        }

        private void EmitPcm()
        {
            if (_n == 0) { return; }
            var b = new byte[_n * 2];
            for (var i = 0; i < _n; i++) { System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2 * i), _pcmBlock[i]); }
            _w.WriteBlock(_t, _samples * 10_000_000 / ClockLockedTrack.OutputRate, b);
            _samples += _n;
            _n = 0;
        }

        public void Complete()
        {
            if (_flac is not null) { _flac.Flush(EmitFlac); }
            else { EmitPcm(); }
        }
    }

    public MatroskaOutput(string path, string title, IReadOnlyList<string> names, MatroskaAudioCodec codec,
        IVideoCapture? camera, int rotation)
    {
        var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        _mkv = new MatroskaWriter(fs, title);
        Files.Add(path);
        if (camera is not null)
        {
            _video = _mkv.AddVideo("Video", camera.Width, camera.Height, camera.FrameRate, rotation);
        }
        var sinks = new List<IMicSink>();
        foreach (var n in names)
        {
            if (codec == MatroskaAudioCodec.Flac)
            {
                var flac = new FlacEncoder(ClockLockedTrack.OutputRate);
                var t = _mkv.AddAudio(n, "A_FLAC", ClockLockedTrack.OutputRate, 1, 16, flac.BuildHeader());
                sinks.Add(new MkSink(_mkv, t, flac));
            }
            else
            {
                var t = _mkv.AddAudio(n, "A_PCM/INT/LIT", ClockLockedTrack.OutputRate, 1, 16);
                sinks.Add(new MkSink(_mkv, t, null));
            }
        }
        Mics = sinks.ToArray();
    }

    public override void Video(byte[] annexB, long timestamp, bool keyFrame)
    {
        if (_video is null) { return; }
        if (!_gotKey)
        {
            if (!keyFrame) { return; }
            var avcC = H264.BuildAvcC(annexB);
            if (avcC is null) { return; }
            _mkv.SetCodecPrivate(_video, avcC);
            _gotKey = true;
        }
        var data = H264.ToLengthPrefixed(annexB);
        if (data.Length > 0) { _mkv.WriteBlock(_video, Math.Max(0, timestamp), data, keyFrame); }
    }

    public override void Complete()
    {
        Exception? first = null;
        foreach (var m in Mics)
        {
            try { m.Complete(); }
            catch (Exception ex) { first ??= ex; }
        }
        _mkv.Dispose();
        if (first is not null) { throw first; }
    }
}

/// <summary>MP4: H.264 + one AAC track per mic.</summary>
internal sealed class Mp4Output : TakeOutput
{
    private readonly Mp4Writer _mp4;
    private readonly Mp4Writer.Track _video;
    private readonly List<(IAacEncoder Enc, Mp4Writer.Track Track)> _aac = [];
    private bool _gotKey;

    private sealed class AacSink(Mp4Writer mp4, Mp4Writer.Track track, IAacEncoder enc) : IMicSink
    {
        public void Write(short[] pcm) => enc.Encode(pcm, s => mp4.WriteSample(track, 0, s.Data.Span));

        public void Complete()
        {
            enc.Flush(s => mp4.WriteSample(track, 0, s.Data.Span));
            if (enc.AudioSpecificConfig.Length > 0) { track.AudioSpecificConfig = enc.AudioSpecificConfig.ToArray(); }
        }
    }

    public Mp4Output(string path, IReadOnlyList<string> names, IAacEncoderFactory aac, int bitRate, IVideoCapture camera, int rotation)
    {
        _mp4 = new Mp4Writer(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16), audioOnly: false);
        Files.Add(path);
        _video = _mp4.AddVideo("Video", camera.Width, camera.Height, rotation);
        var sinks = new List<IMicSink>();
        foreach (var n in names)
        {
            var enc = aac.Create(ClockLockedTrack.OutputRate, 1, bitRate);
            var t = _mp4.AddAudio(n, ClockLockedTrack.OutputRate, 1, enc.AudioSpecificConfig.ToArray());
            t.PrimingSamples = enc.PrimingSamples;
            _aac.Add((enc, t));
            sinks.Add(new AacSink(_mp4, t, enc));
        }
        Mics = sinks.ToArray();
    }

    public override bool VideoDropped => _mp4.VideoDropped || !_gotKey;

    public override void Video(byte[] annexB, long timestamp, bool keyFrame)
    {
        if (!_gotKey)
        {
            if (!keyFrame) { return; }
            var avcC = H264.BuildAvcC(annexB);
            if (avcC is null) { return; }
            _video.AvcC = avcC;
            _gotKey = true;
        }
        var data = H264.ToLengthPrefixed(annexB);
        if (data.Length > 0) { _mp4.WriteSample(_video, Math.Max(0, timestamp), data, keyFrame); }
    }

    public override void Complete()
    {
        Exception? first = null;
        foreach (var m in Mics)
        {
            try { m.Complete(); }
            catch (Exception ex) { first ??= ex; }
        }
        try { _mp4.Dispose(); }
        catch (Exception ex) { first ??= ex; }
        foreach (var (enc, _) in _aac) { enc.Dispose(); }
        if (VideoDropped) { first ??= new IOException("No video was recorded: the camera sent no key frame"); }
        if (first is not null) { throw first; }
    }
}

/// <summary>
/// One take in progress: per-mic <see cref="ClockLockedTrack"/>s on the receive threads feed a bounded queue; a single writer thread
/// encodes and writes. Nothing here blocks the Bluetooth receive thread beyond a short lock.
/// </summary>
internal sealed class TakeRecorder
{
    private const int AudioCapacity = 4096;
    private const int VideoCapacity = 240;

    private readonly struct Work
    {
        public Work(int mic, short[]? pcm, byte[]? video, long ts, bool key) { Mic = mic; Pcm = pcm; Video = video; Ts = ts; Key = key; }
        public readonly int Mic;
        public readonly short[]? Pcm;
        public readonly byte[]? Video;
        public readonly long Ts;
        public readonly bool Key;
    }

    private readonly object _q = new();
    private readonly Queue<Work> _audio = new();
    private readonly Queue<Work> _video = new();
    private bool _completed;
    private long _dropped;

    private readonly TakeOutput _output;
    private readonly bool[] _failed;
    private readonly List<string> _errors = [];
    private readonly ClockLockedTrack[] _tracks;
    private readonly List<(MikeChannel Channel, PcmHandler Handler)> _subs = [];
    private readonly IVideoCapture? _camera;
    private readonly Action<EncodedSample>? _videoHandler;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _t0;
    private bool _stopped;
    private PeakWriter?[] _peaks = [];
    private List<TrackInfo> _sidecarTracks = [];
    private List<TrackInfo> _micTracks = [];
    private bool _sidecarWritten;
    private PosterCapture? _poster;
    private Thread? _writer;

    public string Folder { get; }
    public RecordingFormat Format { get; }

    /// <summary>Host-clock time (MediaClock ticks) that sample 0 of every track belongs to.</summary>
    public long StartTime => _t0;
    public IReadOnlyList<string> Files => _output.Files;
    public IReadOnlyList<(MikeChannel Channel, string? Path, ClockLockedTrack Track)> Mics { get; }

    private TakeRecorder(string folder, RecordingFormat format, TakeOutput output, IReadOnlyList<MikeChannel> channels, IVideoCapture? camera)
    {
        Folder = folder;
        Format = format;
        _output = output;
        _camera = camera;
        _failed = new bool[channels.Count];
        _t0 = MediaClock.Now;
        _tracks = new ClockLockedTrack[channels.Count];
        var mics = new List<(MikeChannel, string?, ClockLockedTrack)>();
        for (var i = 0; i < channels.Count; i++)
        {
            var idx = i;
            _tracks[i] = new ClockLockedTrack(_t0, () => MediaClock.Now, pcm => EnqueueAudio(idx, pcm));
            var path = format.IsMultiTrack() ? output.Files[0] : output.Files[i];
            mics.Add((channels[i], path, _tracks[i]));
        }
        Mics = mics;
        if (camera is not null)
        {
            _videoHandler = s => EnqueueVideo(s.Data.ToArray(), s.Timestamp - _t0, s.IsKeyFrame);
        }
    }

    /// <summary>Creates the output files/encoders (throws on failure, cleaning up) and starts capturing.</summary>
    public static TakeRecorder Start(string folder, string stamp, RecordingSettings settings, IReadOnlyList<MikeChannel> channels,
        IAacEncoderFactory? aac, IVideoCapture? camera)
    {
        var format = settings.Format;
        var names = channels.Select(c => c.Source.DisplayName).ToList();
        var output = CreateOutput(folder, stamp, settings, channels, names, aac, camera);
        TakeRecorder r;
        try
        {
            r = new TakeRecorder(folder, format, output, channels, camera);
        }
        catch
        {
            try { output.Complete(); } catch { /* best effort */ }
            throw;
        }
        // sidecar, peaks and poster are extras: a take must record even when they cannot be set up
        try { r.InitLibrary(settings, names); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { r.Error("library: " + ex.Message); }
        try
        {
            r.Begin();
        }
        catch
        {
            r.Abort();
            throw;
        }
        return r;
    }

    /// <summary>Stops everything after a failed <see cref="Begin"/>: subscriptions, camera, writer thread, files, peaks.</summary>
    private void Abort()
    {
        foreach (var (c, h) in _subs) { try { c.PcmProcessed -= h; } catch { /* best effort */ } }
        _subs.Clear();
        if (_camera is not null)
        {
            try { _camera.SampleEncoded -= _videoHandler; } catch { /* best effort */ }
            try { _camera.StopEncoding(); } catch { /* best effort */ }
        }
        try { _poster?.Dispose(); } catch { /* best effort */ }
        lock (_q) { _completed = true; Monitor.PulseAll(_q); }
        // the writer thread (if it started) completes the output and the peaks; without it do it here
        if (_writer is null || !_writer.IsAlive)
        {
            try { _output.Complete(); } catch { /* best effort */ }
            foreach (var p in _peaks) { try { p?.Dispose(); } catch { /* best effort */ } }
        }
    }

    /// <summary>Writes the sidecar (status recording) and opens one peaks file per mic. Failures are reported in the take's errors, not thrown.</summary>
    private void InitLibrary(RecordingSettings settings, List<string> names)
    {
        var tracks = new List<TrackInfo>();
        var multi = Format.IsMultiTrack();
        var videoFirst = _camera is not null && Format.IsVideo();
        if (videoFirst)
        {
            var file = RelativeFile(_output.Files[0]);
            tracks.Add(new TrackInfo
            {
                Id = TrackInfo.MakeId(file, 0), Kind = TrackKinds.Video, File = file, TrackIndex = 0, Name = "Video", Codec = "h264",
                Width = _camera!.Width, Height = _camera.Height,
                Rotation = _camera.EncodedRotationDegrees, // what the file stores
            });
        }
        var codec = Format switch
        {
            RecordingFormat.Wav => "pcm_s16le",
            RecordingFormat.Mp3 => "mp3",
            RecordingFormat.M4a or RecordingFormat.Mp4 => "aac",
            _ => settings.MatroskaAudio == MatroskaAudioCodec.Flac ? "flac" : "pcm_s16le",
        };
        var micTracks = new List<TrackInfo>();
        for (var i = 0; i < names.Count; i++)
        {
            var file = RelativeFile(Mics[i].Path ?? _output.Files[0]);
            var index = multi ? i + (videoFirst ? 1 : 0) : 0;
            var t = new TrackInfo
            {
                Id = TrackInfo.MakeId(file, index), Kind = TrackKinds.Audio, File = file, TrackIndex = index, Name = names[i],
                Codec = codec, SampleRate = ClockLockedTrack.OutputRate, Channels = 1,
            };
            tracks.Add(t);
            micTracks.Add(t);
        }
        _sidecarTracks = tracks;
        _micTracks = micTracks;
        try
        {
            TakeSidecar.Update(Folder, s =>
            {
                s.Status = TakeStatus.Recording;
                s.Created = DateTime.Now;
                s.Tracks = tracks;
            }, createFolder: true, durable: false);
            _sidecarWritten = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error("sidecar: " + ex.Message);
        }
        _peaks = new PeakWriter?[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            try { _peaks[i] = new PeakWriter(TrackInfo.PeakPath(Folder, micTracks[i].Id), ClockLockedTrack.OutputRate); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Error("peaks: " + ex.Message); }
        }
        if (_camera is not null && Format.IsVideo())
        {
            try { _poster = new PosterCapture(_camera, Folder, _t0) { OnError = Error }; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Error("poster: " + ex.Message); }
        }
    }

    private string RelativeFile(string path) => Path.GetRelativePath(Folder, path).Replace('\\', '/');

    private static TakeOutput CreateOutput(string folder, string stamp, RecordingSettings s, IReadOnlyList<MikeChannel> channels,
        List<string> names, IAacEncoderFactory? aac, IVideoCapture? camera)
    {
        var ext = s.Format.Extension();
        string Single() => Path.Combine(folder, stamp + ext);
        switch (s.Format)
        {
            case RecordingFormat.Mka:
                return new MatroskaOutput(Single(), stamp, names, s.MatroskaAudio, null, 0);
            case RecordingFormat.Mkv:
                return new MatroskaOutput(Single(), stamp, names, s.MatroskaAudio, camera, camera?.EncodedRotationDegrees ?? 0);
            case RecordingFormat.Mp4:
                return new Mp4Output(Single(), names, aac ?? throw new InvalidOperationException("No AAC encoder available."),
                    s.AacBitRate, camera ?? throw new InvalidOperationException("No camera."), camera.EncodedRotationDegrees);
        }

        // one file per mic
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var n in names)
        {
            var name = Sanitize(n);
            var unique = name;
            for (var i = 2; !used.Add(unique); i++) { unique = $"{name}_{i}"; }
            paths.Add(Path.Combine(folder, unique + ext));
        }
        var sinks = new List<IMicSink>();
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                sinks.Add(s.Format switch
                {
                    RecordingFormat.Wav => new WavSink(paths[i]),
                    RecordingFormat.Mp3 => new Mp3Sink(paths[i], s.Mp3BitRate),
                    RecordingFormat.M4a => new M4aSink(paths[i], names[i],
                        (aac ?? throw new InvalidOperationException("No AAC encoder available.")).Create(ClockLockedTrack.OutputRate, 1, s.AacBitRate)),
                    _ => throw new InvalidOperationException($"Unsupported format {s.Format}"),
                });
            }
        }
        catch
        {
            foreach (var sink in sinks) { try { sink.Complete(); } catch { /* best effort */ } }
            throw;
        }
        return new PerMicOutput(sinks.ToArray(), paths);
    }

    private static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => bad.Contains(c) || c == '…' ? '_' : c).ToArray()).Trim();
    }

    private void Begin()
    {
        var thread = new Thread(WriterLoop) { Name = "Plural take writer", IsBackground = true };
        _writer = thread;
        thread.Start();
        for (var i = 0; i < _tracks.Length; i++)
        {
            var track = _tracks[i];
            // After the channel's fader and mute, so mixer moves during the take are recorded.
            var channel = Mics[i].Channel;
            PcmHandler h = (_, mono, rate) => track.Write(mono, rate);
            channel.PcmProcessed += h;
            _subs.Add((channel, h));
        }
        if (_camera is not null)
        {
            _camera.SampleEncoded += _videoHandler;
            _camera.StartEncoding();
        }
    }

    public async Task<RecordedTake> StopAsync()
    {
        var tStop = MediaClock.Now;
        if (!_stopped)
        {
            _stopped = true;
            // Audio for [t0, tStop] is still in flight (bluetooth latency): keep receiving for that long, capped at tStop.
            foreach (var t in _tracks) { t.SetEnd(tStop); }
            var wait = _tracks.Max(t => t.Latency) + TimeSpan.FromMilliseconds(50);
            if (wait > TimeSpan.FromMilliseconds(500)) { wait = TimeSpan.FromMilliseconds(500); }
            if (_tracks.Any(t => t.Latency > TimeSpan.Zero)) { await Task.Delay(wait).ConfigureAwait(false); }
            foreach (var (c, h) in _subs) { c.PcmProcessed -= h; }
            if (_camera is not null)
            {
                _camera.SampleEncoded -= _videoHandler;
                try { _camera.StopEncoding(); } catch (Exception ex) { lock (_errors) { _errors.Add("camera: " + ex.Message); } }
            }
            if (_poster is not null)
            {
                // never lets the poster block or fail the stop (errors go to the take's errors)
                try { await _poster.StopAsync().ConfigureAwait(false); }
                catch (Exception ex) { Error("poster: " + ex.Message); }
            }
            foreach (var t in _tracks) { t.Finish(tStop); }
            lock (_q)
            {
                _completed = true;
                Monitor.PulseAll(_q);
            }
        }
        await _done.Task.ConfigureAwait(false);
        var duration = TimeSpan.FromTicks(tStop - _t0);
        CompleteSidecar(duration);
        List<string> errors;
        lock (_errors) { errors = [.. _errors]; }
        return new RecordedTake(Folder, Format, _output.Files.ToArray(), duration)
        {
            Errors = errors,
            DroppedItems = Interlocked.Read(ref _dropped),
            Tracks = _sidecarWritten ? _sidecarTracks : [],
            SidecarPath = _sidecarWritten ? Path.Combine(Folder, TakeSidecar.FileName) : null,
        };
    }

    // ------------------------------------------------------------------ queue

    private void EnqueueAudio(int mic, short[] pcm)
    {
        lock (_q)
        {
            if (_audio.Count >= AudioCapacity)
            {
                _audio.Dequeue();
                _dropped++;
            }
            _audio.Enqueue(new Work(mic, pcm, null, 0, false));
            Monitor.Pulse(_q);
        }
    }

    private void EnqueueVideo(byte[] data, long ts, bool key)
    {
        lock (_q)
        {
            if (_completed)
            {
                return;
            }
            if (_video.Count >= VideoCapacity)
            {
                _video.Dequeue();
                _dropped++;
            }
            _video.Enqueue(new Work(-1, null, data, ts, key));
            Monitor.Pulse(_q);
        }
    }

    private bool TryTake(out Work w)
    {
        lock (_q)
        {
            while (true)
            {
                if (_audio.Count > 0) { w = _audio.Dequeue(); return true; }
                if (_video.Count > 0) { w = _video.Dequeue(); return true; }
                if (_completed) { w = default; return false; }
                Monitor.Wait(_q);
            }
        }
    }

    private void Error(string msg)
    {
        lock (_errors)
        {
            if (!_errors.Contains(msg)) { _errors.Add(msg); }
        }
    }

    private void WriterLoop()
    {
        try
        {
            while (TryTake(out var w))
            {
                try
                {
                    if (w.Pcm is not null)
                    {
                        if (!_failed[w.Mic])
                        {
                            _output.Mics[w.Mic].Write(w.Pcm);
                            WritePeaks(w.Mic, w.Pcm); // exactly the samples that went into the file
                        }
                    }
                    else if (w.Video is not null)
                    {
                        _output.Video(w.Video, w.Ts, w.Key);
                    }
                }
                catch (Exception ex)
                {
                    if (w.Mic >= 0) { _failed[w.Mic] = true; }
                    Error(ex.Message);
                }
            }
            try { _output.Complete(); }
            catch (Exception ex) { Error(ex.Message); }
            foreach (var p in _peaks)
            {
                try { p?.Complete(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Error("peaks: " + ex.Message); }
            }
        }
        finally
        {
            _done.TrySetResult();
        }
    }

    private void WritePeaks(int mic, short[] pcm)
    {
        if (mic >= _peaks.Length) { return; } // library setup failed: still record
        var p = _peaks[mic];
        if (p is null) { return; }
        try { p.Write(pcm); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _peaks[mic] = null;
            try { p.Dispose(); } catch { /* the file is broken anyway; this releases the handle */ }
            Error("peaks: " + ex.Message);
        }
    }

    /// <summary>Sidecar at the end of the take: status complete and the durations. Failures are reported, not thrown.</summary>
    private void CompleteSidecar(TimeSpan duration)
    {
        if (!_sidecarWritten) { return; }
        for (var i = 0; i < _tracks.Length; i++)
        {
            _micTracks[i].DurationMs = Math.Round(_tracks[i].Duration.TotalMilliseconds, 1);
        }
        foreach (var v in _sidecarTracks.Where(x => x.IsVideo)) { v.DurationMs = Math.Round(duration.TotalMilliseconds, 1); }
        if (_output.VideoDropped)
        {
            // the file holds audio only: drop the video track and shift the audio indexes (ids stay, they name the peaks files)
            _sidecarTracks.RemoveAll(x => x.IsVideo);
            foreach (var t in _micTracks.Where(x => x.TrackIndex > 0)) { t.TrackIndex--; }
        }
        try
        {
            TakeSidecar.Update(Folder, s =>
            {
                s.Status = TakeStatus.Complete;
                s.Tracks = _sidecarTracks;
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error("sidecar: " + ex.Message);
        }
    }
}
