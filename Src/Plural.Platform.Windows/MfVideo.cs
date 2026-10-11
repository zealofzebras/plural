using System.Diagnostics;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using SmartMike.Core.Platform;
using SmartMike.Core.Recording;
using Vortice.MediaFoundation;

namespace SmartMike.Platform.Windows;

/// <summary>Media Foundation camera enumeration and capture (source reader + H.264 encoder MFT).</summary>
[SupportedOSPlatform("windows")]
public sealed class MfVideoPlatform : IVideoPlatform
{
    public IReadOnlyList<CameraInfo> GetCameras()
    {
        var result = new List<CameraInfo>();
        foreach (var act in EnumerateDevices())
        {
            using (act)
            {
                string name = SafeString(act, CaptureDeviceAttributeKeys.FriendlyName) ?? "Camera";
                string id = SafeString(act, CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink) ?? name;
                // No portable panel-location API from Media Foundation; Surface-style names carry "Front"/"Rear".
                bool front = name.Contains("front", StringComparison.OrdinalIgnoreCase);
                result.Add(new CameraInfo(id, name, front));
            }
        }
        return result;
    }

    public Task<IVideoCapture> OpenAsync(CameraInfo camera, VideoSettings settings, CancellationToken ct = default) =>
        Task.Run<IVideoCapture>(() => MfVideoCapture.Open(camera, settings, ct), ct);

    internal static List<IMFActivate> EnumerateDevices()
    {
        MfCommon.EnsureStarted();
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
        return new List<IMFActivate>(MediaFactory.MFEnumDeviceSources(attrs));
    }

    internal static string? SafeString(IMFAttributes a, Guid key)
    {
        try { return a.GetString(key); } catch { return null; }
    }
}

/// <summary>An open camera. See <see cref="IVideoCapture"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class MfVideoCapture : IVideoCapture
{
    private const string PrivacyMessage =
        "Camera access is blocked. Enable Settings -> Privacy & security -> Camera -> \"Let desktop apps access your camera\" and try again.";
    private const int MF_E_HW_MFT_FAILED_START_STREAMING = unchecked((int)0xC00D3704);
    private const int MF_E_NO_MORE_TYPES = unchecked((int)0xC00D36B9);
    private static readonly Guid ReadWriteEnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");

    private readonly VideoSettings _settings;
    private readonly IMFActivate _activate;
    private readonly IMFMediaSource _source;
    private readonly IMFSourceReader _reader;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _cmdGate = new();
    private volatile bool _stop;
    private bool _disposed;

    private Command? _command;
    private MfH264Encoder? _encoder;

    // timestamp mapping
    private bool _tsInitialized, _tsIsQpc;
    private long _tsOffset = long.MaxValue;
    private long _lastTs = long.MinValue;

    // preview
    private readonly byte[][] _previewBuffers = new byte[2][];
    private int _previewIndex;
    private long _lastPreviewTicks;
    private int _previewW, _previewH, _stride;
    private int[]? _xMap;

    public CameraInfo Camera { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FrameRate { get; private set; }

    /// <summary>Name of the encoder MFT in use (valid while encoding / after the last StartEncoding).</summary>
    public string? EncoderName { get; private set; }
    public bool EncoderIsHardware { get; private set; }
    public bool EncoderIsAsync { get; private set; }
    public int EncoderDroppedFrames { get; private set; }
    /// <summary>Native camera type chosen (subtype, size, fps) and which timestamp mode was detected; for diagnostics.</summary>
    public string Diagnostics { get; private set; } = "";
    /// <summary>Encoder selection log (which MFTs were tried and why they were rejected).</summary>
    public IReadOnlyList<string> EncoderLog { get { lock (_spareGate) return _encoderLog.ToArray(); } }
    public long FramesRead { get; private set; }

    public event Action<PreviewFrame>? PreviewFrameReady;
    public event Action<EncodedSample>? SampleEncoded;

    private sealed class Command
    {
        public bool Start;
        public MfH264Encoder? Encoder;
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal static MfVideoCapture Open(CameraInfo camera, VideoSettings settings, CancellationToken ct)
    {
        MfCommon.EnsureStarted();
        IMFActivate? match = null;
        var all = MfVideoPlatform.EnumerateDevices();
        try
        {
            foreach (var a in all)
            {
                if (match is null && MfVideoPlatform.SafeString(a, CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink) == camera.Id) { match = a; continue; }
            }
            if (match is null) throw new InvalidOperationException($"Camera not found: {camera.Name}");
            foreach (var a in all) if (!ReferenceEquals(a, match)) a.Dispose();
        }
        catch { foreach (var a in all) a.Dispose(); throw; }

        try { return new MfVideoCapture(camera, settings, match, ct); }
        catch { match.Dispose(); throw; }
    }

    private MfVideoCapture(CameraInfo camera, VideoSettings settings, IMFActivate activate, CancellationToken ct)
    {
        Camera = camera;
        _settings = settings;
        _activate = activate;

        try
        {
            activate.ActivateObject<IMFMediaSource>(MfCommon.IID_IMFMediaSource, out var src);
            _source = src!;

            using var attrs = MediaFactory.MFCreateAttributes(3);
            attrs.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
            attrs.Set(ReadWriteEnableHardwareTransforms, 1u);
            _reader = MediaFactory.MFCreateSourceReaderFromMediaSource(_source, attrs);
            ConfigureReader();
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            throw new UnauthorizedAccessException(PrivacyMessage, ex);
        }

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "MfVideoCapture" };
        _thread.Start();
        PrepareEncoder();

        // Wait for the first frame so permission / device errors surface from OpenAsync.
        try
        {
            if (!_firstFrame.Task.Wait(TimeSpan.FromSeconds(10), ct))
                throw new TimeoutException("The camera produced no frames. " + PrivacyMessage);
        }
        catch (AggregateException ae)
        {
            Teardown();
            throw ae.InnerException ?? ae;
        }
        catch
        {
            Teardown();
            throw;
        }
    }

    private static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException ||
        (ex is SharpGenException sg && (sg.ResultCode.Code == MfCommon.E_ACCESSDENIED || sg.ResultCode.Code == MF_E_HW_MFT_FAILED_START_STREAMING));

    // ---------------------------------------------------------------- reader setup

    private void ConfigureReader()
    {
        const SourceReaderIndex stream = SourceReaderIndex.FirstVideoStream;
        _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        _reader.SetStreamSelection(stream, true);

        IMFMediaType? best = null;
        double bestScore = double.MaxValue;
        string bestDesc = "";
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _reader.GetNativeMediaType(stream, i); }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_NO_MORE_TYPES) { break; }

            Guid sub = t.GetGUID(MediaTypeAttributeKeys.Subtype);
            ulong size = t.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            ulong rate = t.GetUInt64(MediaTypeAttributeKeys.FrameRate);
            int w = (int)(size >> 32), h = (int)(size & 0xFFFFFFFF);
            double fps = (uint)(rate & 0xFFFFFFFF) == 0 ? 0 : (double)(rate >> 32) / (uint)(rate & 0xFFFFFFFF);

            double subPenalty = sub == VideoFormatGuids.NV12 ? 0 : sub == VideoFormatGuids.YUY2 ? 1 : sub == VideoFormatGuids.Mjpg ? 2 : 50;
            double dim = Math.Abs(w - _settings.Width) + Math.Abs(h - _settings.Height);
            double fpsPenalty = fps < _settings.FrameRate - 0.5 ? 5000 + (_settings.FrameRate - fps) * 100 : (fps - _settings.FrameRate) * 20;
            double score = dim * 10 + fpsPenalty + subPenalty * 5;
            if (w < 2 || h < 2) score += 1e9;

            if (score < bestScore)
            {
                best?.Dispose();
                best = t;
                bestScore = score;
                bestDesc = $"{w}x{h}@{fps:0.#} {FourCcName(sub)}";
            }
            else t.Dispose();
        }
        if (best is null) throw new InvalidOperationException("The camera reports no video formats.");

        using (best)
        {
            using var chosen = MediaFactory.MFCreateMediaType();
            best.CopyAllItems(chosen);
            chosen.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            chosen.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            _reader.SetCurrentMediaType(stream, chosen);
        }

        ReadCurrentType(stream);
        Diagnostics = $"native {bestDesc} -> NV12 {Width}x{Height}@{FrameRate}";
    }

    private void ReadCurrentType(SourceReaderIndex stream)
    {
        using var cur = _reader.GetCurrentMediaType(stream);
        ulong size = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        ulong rate = cur.GetUInt64(MediaTypeAttributeKeys.FrameRate);
        Width = (int)(size >> 32) & ~1;
        Height = (int)(size & 0xFFFFFFFF) & ~1;
        uint den = (uint)(rate & 0xFFFFFFFF);
        FrameRate = den == 0 ? _settings.FrameRate : (int)Math.Round((double)(rate >> 32) / den);
        if (FrameRate <= 0) FrameRate = _settings.FrameRate;
        int stride = 0;
        try { stride = (int)cur.GetUInt32(MediaTypeAttributeKeys.DefaultStride); } catch { }
        _stride = stride > 0 ? stride : Width;

        _previewW = Math.Min(640, Width) & ~1;
        _previewH = (Width == 0 ? 0 : (int)((long)Height * _previewW / Width)) & ~1;
        _previewBuffers[0] = new byte[_previewW * _previewH * 4];
        _previewBuffers[1] = new byte[_previewW * _previewH * 4];
        _xMap = new int[_previewW];
        for (int x = 0; x < _previewW; x++) _xMap[x] = x * Width / _previewW;
    }

    private static string FourCcName(Guid g)
    {
        if (g == VideoFormatGuids.NV12) return "NV12";
        if (g == VideoFormatGuids.YUY2) return "YUY2";
        if (g == VideoFormatGuids.Mjpg) return "MJPG";
        var b = g.ToByteArray();
        return new string(new[] { (char)b[0], (char)b[1], (char)b[2], (char)b[3] });
    }

    // ---------------------------------------------------------------- capture thread

    private void CaptureLoop()
    {
        try
        {
            while (!_stop)
            {
                IMFSample? sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                    out _, out SourceReaderFlag flags, out long deviceTs);
                try
                {
                    if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                    if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0) ReadCurrentType(SourceReaderIndex.FirstVideoStream);
                    ServiceCommand();
                    if (sample is null) continue;

                    FramesRead++;
                    _firstFrame.TrySetResult();
                    long ts = MapTimestamp(deviceTs);
                    sample.SampleTime = ts;
                    sample.SampleDuration = 10_000_000L / FrameRate;

                    EmitPreview(sample, ts);

                    if (_encoder is { } enc)
                    {
                        var s = sample;
                        sample = null;   // encoder takes ownership
                        try { enc.Encode(s, RaiseSample); }
                        catch (Exception ex) { Debug.WriteLine("H.264 encode failed: " + ex.Message); }
                    }
                }
                finally { sample?.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Camera capture stopped: " + ex);
            if (IsAccessDenied(ex)) _firstFrame.TrySetException(new UnauthorizedAccessException(PrivacyMessage, ex));
            else _firstFrame.TrySetException(ex);
        }
        finally
        {
            _firstFrame.TrySetException(new InvalidOperationException("The camera stopped before delivering a frame."));
            // Release any waiting command.
            Command? c;
            lock (_cmdGate) { c = _command; _command = null; }
            c?.Done.TrySetException(new ObjectDisposedException(nameof(MfVideoCapture)));
            CloseEncoder(flush: false);
        }
    }

    private void RaiseSample(EncodedSample s)
    {
        try { SampleEncoded?.Invoke(s); }
        catch (Exception ex) { Debug.WriteLine("SampleEncoded handler failed: " + ex.Message); }
    }

    private void ServiceCommand()
    {
        Command? c;
        lock (_cmdGate) { c = _command; _command = null; }
        if (c is null) return;
        try
        {
            if (c.Start)
            {
                CloseEncoder(flush: false);
                var enc = c.Encoder!;
                _encoder = enc;
                EncoderName = enc.Name;
                EncoderIsHardware = enc.IsHardware;
                EncoderIsAsync = enc.IsAsync;
            }
            else { CloseEncoder(flush: true); PrepareEncoder(); }
            c.Done.TrySetResult();
        }
        catch (Exception ex) { c.Done.TrySetException(ex); }
    }

    private void CloseEncoder(bool flush)
    {
        var enc = _encoder;
        _encoder = null;
        if (enc is null) return;
        try { if (flush) enc.Flush(RaiseSample); }
        catch (Exception ex) { Debug.WriteLine("H.264 flush failed: " + ex.Message); }
        EncoderDroppedFrames = enc.DroppedFrames;
        enc.Dispose();
    }

    /// <summary>
    /// Maps the reader's 100 ns timestamp to MediaClock ticks. Cameras normally stamp with the QPC-based system time, which is the
    /// same time base as Stopwatch/MediaClock; this is detected on the first frame (|device - now| &lt; 5 s). Otherwise an offset is
    /// tracked as the minimum of (now - device), which removes scheduling jitter.
    /// </summary>
    private long MapTimestamp(long deviceTs)
    {
        long now = MediaClock.Now;
        if (!_tsInitialized)
        {
            _tsInitialized = true;
            _tsIsQpc = Math.Abs(now - deviceTs) < 50_000_000L;
            Diagnostics += _tsIsQpc ? " | ts: device QPC" : " | ts: offset-tracked";
        }
        long ts;
        if (_tsIsQpc) ts = deviceTs;
        else
        {
            _tsOffset = Math.Min(_tsOffset, now - deviceTs);
            ts = deviceTs + _tsOffset;
        }
        if (ts <= _lastTs) ts = _lastTs + 1;
        _lastTs = ts;
        return ts;
    }

    // ---------------------------------------------------------------- preview

    private void EmitPreview(IMFSample sample, long ts)
    {
        var handler = PreviewFrameReady;
        if (handler is null) return;
        long nowTicks = Stopwatch.GetTimestamp();
        long interval = Stopwatch.Frequency / 15;
        if (nowTicks < _lastPreviewTicks) return;   // next frame due at _lastPreviewTicks (~15 fps)
        _lastPreviewTicks = (_lastPreviewTicks == 0 || nowTicks - _lastPreviewTicks > interval) ? nowTicks + interval : _lastPreviewTicks + interval;

        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr ptr, out _, out int length);
        try
        {
            int stride = _stride;
            if (length < stride * Height * 3 / 2) return;
            var dst = _previewBuffers[_previewIndex];
            _previewIndex ^= 1;
            Nv12ToBgraScaled((byte*)ptr, stride, Width, Height, dst, _previewW, _previewH, _xMap!);
            handler(new PreviewFrame(dst, _previewW, _previewH, _previewW * 4));
        }
        finally { buffer.Unlock(); }
    }

    private static void Nv12ToBgraScaled(byte* src, int stride, int w, int h, byte[] dst, int dw, int dh, int[] xMap)
    {
        byte* yPlane = src;
        byte* uvPlane = src + (long)stride * h;
        bool hd = h >= 720;
        fixed (byte* d0 = dst)
        {
            for (int y = 0; y < dh; y++)
            {
                int sy = y * h / dh;
                byte* yRow = yPlane + (long)sy * stride;
                byte* uvRow = uvPlane + (long)(sy >> 1) * stride;
                byte* o = d0 + (long)y * dw * 4;
                for (int x = 0; x < dw; x++)
                {
                    int sx = xMap[x];
                    int yy = yRow[sx] - 16;
                    int u = uvRow[sx & ~1] - 128;
                    int v = uvRow[(sx & ~1) + 1] - 128;
                    if (yy < 0) yy = 0;
                    int c = 298 * yy;
                    int r, g, b;
                    if (hd) { r = (c + 459 * v + 128) >> 8; g = (c - 55 * u - 136 * v + 128) >> 8; b = (c + 541 * u + 128) >> 8; }
                    else { r = (c + 409 * v + 128) >> 8; g = (c - 100 * u - 208 * v + 128) >> 8; b = (c + 516 * u + 128) >> 8; }
                    o[0] = Clamp(b); o[1] = Clamp(g); o[2] = Clamp(r); o[3] = 255;
                    o += 4;
                }
            }
        }
    }

    private static byte Clamp(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    // ---------------------------------------------------------------- encoder pre-warming
    // Creating a hardware H.264 encoder can take seconds (Snapdragon: ~3 s), which would stall the capture thread and delay
    // StartEncoding. A spare encoder is therefore created in the background right after opening and after each StopEncoding.

    private Task<MfH264Encoder>? _spare;
    private readonly object _spareGate = new();
    private readonly List<string> _encoderLog = new();

    private void PrepareEncoder()
    {
        lock (_spareGate)
        {
            if (_spare is not null || _disposed) return;
            int w = Width, h = Height, fps = FrameRate, br = _settings.BitRate;
            _spare = Task.Run(() => MfH264Encoder.Create(w, h, fps, br, allowHardware: Environment.GetEnvironmentVariable("SMARTMIKE_SOFTWARE_H264") != "1", _encoderLog));
        }
    }

    private MfH264Encoder TakeEncoder()
    {
        Task<MfH264Encoder> t;
        lock (_spareGate)
        {
            PrepareEncoder();
            t = _spare!;
            _spare = null;
        }
        try
        {
            if (!t.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("The H.264 encoder did not initialise.");
            return t.Result;
        }
        catch (AggregateException ae) { throw ae.InnerException ?? ae; }
    }

    // ---------------------------------------------------------------- IVideoCapture

    public void StartEncoding() => Execute(start: true);

    public void StopEncoding() => Execute(start: false);

    private void Execute(bool start)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var c = new Command { Start = start };
        if (start) c.Encoder = TakeEncoder();
        lock (_cmdGate) _command = c;
        if (!c.Done.Task.Wait(TimeSpan.FromSeconds(start ? 10 : 5)))
        {
            lock (_cmdGate) { if (ReferenceEquals(_command, c)) _command = null; }
            c.Encoder?.Dispose();
            throw new TimeoutException("The camera did not respond (no frames arriving).");
        }
        c.Done.Task.GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        return new ValueTask(Task.Run(Teardown));
    }

    private void Teardown()
    {
        _stop = true;
        // A blocking ReadSample returns when the source shuts down, so stop the source first if the thread does not exit in time.
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            try { _source.Shutdown(); } catch { }
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        lock (_spareGate)
        {
            var sp = _spare; _spare = null;
            try { if (sp is not null && sp.Wait(TimeSpan.FromSeconds(10))) sp.Result.Dispose(); } catch { }
        }
        try { _activate.ShutdownObject(); } catch { }
        try { _source.Shutdown(); } catch { }
        _reader.Dispose();
        _source.Dispose();
        _activate.Dispose();
    }
}
