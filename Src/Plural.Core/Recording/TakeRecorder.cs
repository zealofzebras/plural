using SmartMike.Core.Audio;
using SmartMike.Core.Platform;
using SmartMike.Core.Session;

namespace SmartMike.Core.Recording;

/// <summary>Result of a finished recording.</summary>
public sealed record RecordedTake(string Folder, RecordingFormat Format, IReadOnlyList<string> Files, TimeSpan Duration)
{
    /// <summary>Non-fatal problems on the writer thread (empty when everything went fine).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Queue items dropped because the writer could not keep up (0 normally).</summary>
    public long DroppedItems { get; init; }
}

/// <summary>Zip/share helper for finished takes.</summary>
public static class TakeExport
{
    /// <summary>
    /// The single media file of the take, or (for several files) <c>&lt;takeFolder&gt;.zip</c> next to the folder, created or reused
    /// when it is newer than every file in the take.
    /// </summary>
    public static string GetShareFile(string takeFolder)
    {
        var folder = takeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = Directory.GetFiles(folder).Where(f => !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            throw new FileNotFoundException("The take folder contains no recordings.", folder);
        }
        if (files.Count == 1)
        {
            return files[0];
        }
        var zip = folder + ".zip";
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
                var e = za.CreateEntry(Path.GetFileName(f), System.IO.Compression.CompressionLevel.Fastest);
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
            _aac.Add((enc, t));
            sinks.Add(new AacSink(_mp4, t, enc));
        }
        Mics = sinks.ToArray();
    }

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
    private readonly List<(MikeSession Session, PcmHandler Handler)> _subs = [];
    private readonly IVideoCapture? _camera;
    private readonly Action<EncodedSample>? _videoHandler;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _t0;
    private bool _stopped;

    public string Folder { get; }
    public RecordingFormat Format { get; }
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
        var names = channels.Select(c => c.Session.DisplayName).ToList();
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
        r.Begin();
        return r;
    }

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
                return new MatroskaOutput(Single(), stamp, names, s.MatroskaAudio, camera, camera?.RotationDegrees ?? 0);
            case RecordingFormat.Mp4:
                return new Mp4Output(Single(), names, aac ?? throw new InvalidOperationException("No AAC encoder available."),
                    s.AacBitRate, camera ?? throw new InvalidOperationException("No camera."), camera.RotationDegrees);
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
        var thread = new Thread(WriterLoop) { Name = "SmartMike take writer", IsBackground = true };
        thread.Start();
        for (var i = 0; i < _tracks.Length; i++)
        {
            var track = _tracks[i];
            var session = Mics[i].Channel.Session;
            PcmHandler h = (_, mono, rate) => track.Write(mono, rate);
            session.PcmReceived += h;
            _subs.Add((session, h));
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
            foreach (var (s, h) in _subs) { s.PcmReceived -= h; }
            if (_camera is not null)
            {
                _camera.SampleEncoded -= _videoHandler;
                try { _camera.StopEncoding(); } catch (Exception ex) { lock (_errors) { _errors.Add("camera: " + ex.Message); } }
            }
            foreach (var t in _tracks) { t.Finish(tStop); }
            lock (_q)
            {
                _completed = true;
                Monitor.PulseAll(_q);
            }
        }
        await _done.Task.ConfigureAwait(false);
        List<string> errors;
        lock (_errors) { errors = [.. _errors]; }
        return new RecordedTake(Folder, Format, _output.Files.ToArray(), TimeSpan.FromTicks(tStop - _t0))
        {
            Errors = errors,
            DroppedItems = Interlocked.Read(ref _dropped),
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
                        if (!_failed[w.Mic]) { _output.Mics[w.Mic].Write(w.Pcm); }
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
        }
        finally
        {
            _done.TrySetResult();
        }
    }
}
