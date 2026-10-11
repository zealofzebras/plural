using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using SmartMike.Core.Platform;
using SmartMike.Core.Recording;

namespace SmartMike.Platform.Android;

/// <summary>Camera2 enumeration and capture. Preview (YUV ImageReader) and the H.264 encoder surface share one capture session.</summary>
public sealed class AndroidVideoPlatform(Context context) : IVideoPlatform
{
    private CameraManager Manager => (CameraManager)context.GetSystemService(Context.CameraService)!;

    public IReadOnlyList<CameraInfo> GetCameras()
    {
        var list = new List<CameraInfo>();
        var manager = Manager;
        var ids = manager.GetCameraIdList() ?? [];
        var backCount = 0;
        var frontCount = 0;
        foreach (var id in ids)
        {
            var facing = CameraHelpers.GetInt(manager.GetCameraCharacteristics(id), CameraCharacteristics.LensFacing, -1);
            if (!CameraHelpers.HasVideoSizes(manager.GetCameraCharacteristics(id)))
            {
                continue;
            }
            string name;
            var front = false;
            if (facing == (int)LensFacing.Front)
            {
                front = true;
                name = ++frontCount == 1 ? "Front camera" : $"Front camera {frontCount}";
            }
            else if (facing == (int)LensFacing.Back)
            {
                name = ++backCount == 1 ? "Back camera" : $"Back camera {backCount}";
            }
            else
            {
                name = "External camera";
            }
            list.Add(new CameraInfo(id, $"{name} ({id})", front));
        }
        // Back cameras first.
        return list.OrderBy(c => c.IsFrontFacing).ToList();
    }

    public async Task<IVideoCapture> OpenAsync(CameraInfo camera, VideoSettings settings, CancellationToken ct = default)
    {
        if (context.CheckSelfPermission(global::Android.Manifest.Permission.Camera) != Permission.Granted)
        {
            throw new InvalidOperationException("Camera permission has not been granted.");
        }
        var capture = new AndroidVideoCapture(context, Manager, camera, settings);
        try
        {
            await capture.OpenAsync(ct).ConfigureAwait(false);
            return capture;
        }
        catch
        {
            await capture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

internal static class CameraHelpers
{
    public static int GetInt(CameraCharacteristics chars, CameraCharacteristics.Key? key, int fallback)
    {
        var o = chars.Get(key!);
        return o is null ? fallback : o.JavaCast<Java.Lang.Integer>().IntValue();
    }

    public static StreamConfigurationMap? GetMap(CameraCharacteristics chars) =>
        chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap) as StreamConfigurationMap;

    public static bool HasVideoSizes(CameraCharacteristics chars) =>
        GetMap(chars)?.GetOutputSizes(Java.Lang.Class.FromType(typeof(MediaRecorder)))?.Length > 0;
}

/// <summary>
/// Threading: Camera2 callbacks and the preview ImageReader run on one HandlerThread ("camera"); encoder output is drained on a
/// separate thread. <see cref="PreviewFrameReady"/> and <see cref="SampleEncoded"/> are raised on those threads.
/// </summary>
public sealed class AndroidVideoCapture : IVideoCapture
{
    private const long PreviewIntervalNs = 1_000_000_000L / 15 - 5_000_000L;
    private const int MaxPreviewWidth = 640;

    private readonly Context _context;
    private readonly CameraManager _manager;
    private readonly VideoSettings _settings;

    private HandlerThread? _cameraThread;
    private Handler? _handler;
    private CameraDevice? _device;
    private CameraCaptureSession? _session;
    private ImageReader? _reader;
    private MediaCodec? _codec;
    private Surface? _codecSurface;
    private Thread? _drainThread;
    private volatile bool _stopDrain;
    private bool _disposed;

    // Camera timestamp -> MediaClock mapping.
    private long _mediaTicksAtOpen;
    private long _cameraNsAtOpen;

    private volatile bool _encoding;
    private volatile bool _waitForKeyFrame;
    private byte[]? _config;

    // Preview conversion state.
    private long _lastPreviewNs;
    private byte[] _yBuf = [], _uBuf = [], _vBuf = [];
    private readonly byte[][] _bgra = [[], []];
    private int _bgraIndex;

    public CameraInfo Camera { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FrameRate { get; private set; }

    /// <summary>Clockwise degrees the camera image must be rotated to be upright in the device's natural orientation
    /// (SENSOR_ORIENTATION). Preview frames and the encoded video are NOT rotated; both are in sensor orientation.</summary>
    public int SensorOrientation { get; private set; }

    /// <summary>Clockwise degrees to rotate frames so they appear upright for the current device orientation.</summary>
    public int RotationDegrees
    {
        get
        {
            var display = DisplayDegrees();
            return Camera.IsFrontFacing
                ? (SensorOrientation + display) % 360
                : (SensorOrientation - display + 360) % 360;
        }
    }

    public bool MirrorPreview => Camera.IsFrontFacing;

    private int DisplayDegrees()
    {
        Display? display = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            try { display = _context.Display; } catch (Java.Lang.UnsupportedOperationException) { }
        }
        if (display is null)
        {
#pragma warning disable CA1422 // Display from WindowManager for non-visual contexts.
            display = (_context.GetSystemService(Context.WindowService) as IWindowManager)?.DefaultDisplay;
#pragma warning restore CA1422
        }
        return display?.Rotation switch
        {
            SurfaceOrientation.Rotation90 => 90,
            SurfaceOrientation.Rotation180 => 180,
            SurfaceOrientation.Rotation270 => 270,
            _ => 0,
        };
    }

    public event Action<PreviewFrame>? PreviewFrameReady;
    public event Action<EncodedSample>? SampleEncoded;

    internal AndroidVideoCapture(Context context, CameraManager manager, CameraInfo camera, VideoSettings settings)
    {
        _context = context;
        _manager = manager;
        _settings = settings;
        Camera = camera;
    }

    internal async Task OpenAsync(CancellationToken ct)
    {
        var chars = _manager.GetCameraCharacteristics(Camera.Id);
        var map = CameraHelpers.GetMap(chars) ?? throw new InvalidOperationException("Camera has no stream configurations.");
        SensorOrientation = CameraHelpers.GetInt(chars, CameraCharacteristics.SensorOrientation, 0);

        var size = ChooseSize(map.GetOutputSizes(Java.Lang.Class.FromType(typeof(MediaRecorder))), _settings.Width, _settings.Height);
        Width = size.Width;
        Height = size.Height;
        var fpsRange = ChooseFpsRange(chars, _settings.FrameRate);
        FrameRate = fpsRange is { } r ? r.Upper : _settings.FrameRate;
        var previewSize = ChoosePreviewSize(map.GetOutputSizes((int)ImageFormatType.Yuv420888), Width, Height);

        _cameraThread = new HandlerThread("smartmike-camera");
        _cameraThread.Start();
        _handler = new Handler(_cameraThread.Looper!);

        CreateEncoder();
        _reader = ImageReader.NewInstance(previewSize.Width, previewSize.Height, ImageFormatType.Yuv420888, 2)!;
        _reader.SetOnImageAvailableListener(new ImageListener(this), _handler);

        // Sample both clocks together so encoder presentation times can be mapped to MediaClock.
        var realtime = CameraHelpers.GetInt(chars, CameraCharacteristics.SensorInfoTimestampSource, 0) == 1;
        _mediaTicksAtOpen = MediaClock.Now;
        _cameraNsAtOpen = realtime ? SystemClock.ElapsedRealtimeNanos() : Java.Lang.JavaSystem.NanoTime();

        var opened = new TaskCompletionSource<CameraDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => opened.TrySetCanceled(ct));
        _manager.OpenCamera(Camera.Id, new DeviceCallback(opened), _handler);
        _device = await opened.Task.ConfigureAwait(false);

        var configured = new TaskCompletionSource<CameraCaptureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg2 = ct.Register(() => configured.TrySetCanceled(ct));
        var surfaces = new List<Surface> { _reader.Surface!, _codecSurface! };
#pragma warning disable CA1422 // SessionConfiguration needs API 28; the legacy overload works from API 21.
#pragma warning disable CS0618
        _device.CreateCaptureSession(surfaces, new SessionCallback(configured), _handler);
#pragma warning restore CS0618
#pragma warning restore CA1422
        _session = await configured.Task.ConfigureAwait(false);

        var builder = _device.CreateCaptureRequest(CameraTemplate.Record)!;
        builder.AddTarget(_reader.Surface!);
        builder.AddTarget(_codecSurface!);
        if (fpsRange is { } fr)
        {
            builder.Set(CaptureRequest.ControlAeTargetFpsRange!, new global::Android.Util.Range(Java.Lang.Integer.ValueOf(fr.Lower), Java.Lang.Integer.ValueOf(fr.Upper)));
        }
        builder.Set(CaptureRequest.ControlMode!, (int)ControlMode.Auto);
        builder.Set(CaptureRequest.ControlAfMode!, (int)ControlAFMode.ContinuousVideo);
        _session.SetRepeatingRequest(builder.Build()!, null, _handler);
    }

    private static global::Android.Util.Size ChooseSize(global::Android.Util.Size[]? sizes, int w, int h)
    {
        if (sizes is not { Length: > 0 })
        {
            throw new InvalidOperationException("Camera reports no video sizes.");
        }
        // Camera sizes are landscape; accept a portrait request too.
        if (h > w)
        {
            (w, h) = (h, w);
        }
        return sizes
            .Where(s => s.Width % 2 == 0 && s.Height % 2 == 0 && s.Width <= 4096)
            .OrderBy(s => Math.Abs((long)s.Width * s.Height - (long)w * h) + Math.Abs(s.Width * h - s.Height * w) * 4L)
            .FirstOrDefault() ?? sizes[0];
    }

    private static global::Android.Util.Size ChoosePreviewSize(global::Android.Util.Size[]? sizes, int encW, int encH)
    {
        if (sizes is not { Length: > 0 })
        {
            throw new InvalidOperationException("Camera reports no YUV preview sizes.");
        }
        var candidates = sizes.Where(s => s.Width <= MaxPreviewWidth).ToArray();
        if (candidates.Length == 0)
        {
            return sizes.OrderBy(s => s.Width).First();
        }
        // Same aspect ratio as the encoded video if possible, then the largest.
        return candidates
            .OrderBy(s => Math.Abs(s.Width * encH - s.Height * encW))
            .ThenByDescending(s => s.Width)
            .First();
    }

    private static (int Lower, int Upper)? ChooseFpsRange(CameraCharacteristics chars, int fps)
    {
        var o = chars.Get(CameraCharacteristics.ControlAeAvailableTargetFpsRanges);
        if (o is null)
        {
            return null;
        }
        var ranges = JNIEnv.GetArray<global::Android.Util.Range>(o.Handle);
        if (ranges is not { Length: > 0 })
        {
            return null;
        }
        (int Lower, int Upper)? best = null;
        var bestScore = long.MaxValue;
        foreach (var rg in ranges)
        {
            var lo = rg.Lower!.JavaCast<Java.Lang.Integer>().IntValue();
            var hi = rg.Upper!.JavaCast<Java.Lang.Integer>().IntValue();
            // Prefer a fixed range at the requested rate, then the closest upper bound, then the tightest range.
            var score = Math.Abs(hi - fps) * 1000L + (hi - lo) * 10L + (hi < fps ? 1 : 0);
            if (score < bestScore)
            {
                bestScore = score;
                best = (lo, hi);
            }
        }
        return best;
    }

    private void CreateEncoder()
    {
        var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, Width, Height);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, _settings.BitRate);
        format.SetInteger(MediaFormat.KeyFrameRate, FrameRate);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 1);
        _codec = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc)!;
        _codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        _codecSurface = _codec.CreateInputSurface();
        _codec.Start();
        _drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "smartmike-h264" };
        _drainThread.Start();
    }

    public void StartEncoding()
    {
        if (_disposed || _codec is null)
        {
            return;
        }
        _waitForKeyFrame = true;
        _encoding = true;
        var p = new Bundle();
        p.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0);
        try { _codec.SetParameters(p); } catch (Java.Lang.IllegalStateException) { }
    }

    public void StopEncoding() => _encoding = false;

    private long ToMediaTicks(long cameraUs) => _mediaTicksAtOpen + (cameraUs * 1000L - _cameraNsAtOpen) / 100L;

    private void DrainLoop()
    {
        var info = new MediaCodec.BufferInfo();
        var codec = _codec!;
        while (!_stopDrain)
        {
            int index;
            try
            {
                index = codec.DequeueOutputBuffer(info, 100_000);
            }
            catch (Java.Lang.IllegalStateException)
            {
                return;
            }
            if (index < 0)
            {
                continue;
            }
            try
            {
                var flags = info.Flags;
                if (info.Size > 0)
                {
                    var buf = codec.GetOutputBuffer(index)!;
                    buf.Position(info.Offset);
                    buf.Limit(info.Offset + info.Size);
                    if (flags.HasFlag(MediaCodecBufferFlags.CodecConfig))
                    {
                        var cfg = new byte[info.Size];
                        buf.Get(cfg);
                        _config = cfg;
                    }
                    else if (_encoding)
                    {
                        var key = flags.HasFlag(MediaCodecBufferFlags.KeyFrame);
                        if (_waitForKeyFrame && !key)
                        {
                            // Discard until the requested sync frame arrives.
                        }
                        else
                        {
                            var cfg = key ? _config : null;
                            var data = new byte[(cfg?.Length ?? 0) + info.Size];
                            cfg?.CopyTo(data, 0);
                            buf.Get(data, cfg?.Length ?? 0, info.Size);
                            if (key)
                            {
                                _waitForKeyFrame = false;
                            }
                            SampleEncoded?.Invoke(new EncodedSample(data, ToMediaTicks(info.PresentationTimeUs), key));
                        }
                    }
                }
            }
            catch (Exception) when (_stopDrain)
            {
            }
            finally
            {
                try { codec.ReleaseOutputBuffer(index, false); } catch (Java.Lang.IllegalStateException) { }
            }
        }
    }

    private void OnImage(ImageReader reader)
    {
        using var image = reader.AcquireLatestImage();
        if (image is null)
        {
            return;
        }
        var now = Java.Lang.JavaSystem.NanoTime();
        if (now - _lastPreviewNs < PreviewIntervalNs || PreviewFrameReady is not { } handler)
        {
            return;
        }
        _lastPreviewNs = now;
        var planes = image.GetPlanes();
        if (planes is not { Length: 3 })
        {
            return;
        }
        int w = image.Width, h = image.Height;
        var y = planes[0];
        var u = planes[1];
        var v = planes[2];
        Fill(y.Buffer!, ref _yBuf, y.RowStride * h);
        Fill(u.Buffer!, ref _uBuf, u.RowStride * (h / 2) + u.PixelStride);
        Fill(v.Buffer!, ref _vBuf, v.RowStride * (h / 2) + v.PixelStride);

        var stride = w * 4;
        _bgraIndex ^= 1;
        ref var outBuf = ref _bgra[_bgraIndex];
        if (outBuf.Length != stride * h)
        {
            outBuf = new byte[stride * h];
        }
        YuvToBgra(_yBuf, y.RowStride, _uBuf, _vBuf, u.RowStride, u.PixelStride, w, h, outBuf, stride);
        handler(new PreviewFrame(outBuf, w, h, stride));
    }

    private static void Fill(Java.Nio.ByteBuffer buffer, ref byte[] array, int needed)
    {
        if (array.Length < needed)
        {
            array = new byte[needed];
        }
        buffer.Rewind();
        var n = Math.Min(buffer.Remaining(), array.Length);
        buffer.Get(array, 0, n);
    }

    /// <summary>Full-range BT.601 YUV 4:2:0 to BGRA, fixed point.</summary>
    private static void YuvToBgra(ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int uvStride, int uvPixel,
        int width, int height, Span<byte> dst, int dstStride)
    {
        for (var j = 0; j < height; j++)
        {
            var yRow = y.Slice(j * yStride, width);
            var uvRow = (j >> 1) * uvStride;
            var d = dst.Slice(j * dstStride, width * 4);
            for (var i = 0; i < width; i++)
            {
                var uvi = uvRow + (i >> 1) * uvPixel;
                int c = yRow[i];
                var du = u[uvi] - 128;
                var dv = v[uvi] - 128;
                var r = c + ((359 * dv) >> 8);
                var g = c - ((88 * du + 183 * dv) >> 8);
                var b = c + ((454 * du) >> 8);
                var o = i * 4;
                d[o] = Clamp(b);
                d[o + 1] = Clamp(g);
                d[o + 2] = Clamp(r);
                d[o + 3] = 255;
            }
        }
    }

    private static byte Clamp(int x) => (byte)(x < 0 ? 0 : x > 255 ? 255 : x);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _encoding = false;
        await Task.Run(() =>
        {
            // Stop the producers first (session, camera, reader), then the encoder, then the thread.
            try { _session?.StopRepeating(); } catch (Java.Lang.Exception) { }
            try { _session?.Close(); } catch (Java.Lang.Exception) { }
            try { _device?.Close(); } catch (Java.Lang.Exception) { }
            _reader?.SetOnImageAvailableListener(null, null);
            _reader?.Close();

            _stopDrain = true;
            _drainThread?.Join(2000);
            if (_codec is not null)
            {
                try { _codec.Stop(); } catch (Java.Lang.IllegalStateException) { }
                _codec.Release();
            }
            _codecSurface?.Release();

            _cameraThread?.QuitSafely();
            _cameraThread?.Join();
        }).ConfigureAwait(false);
    }

    private sealed class ImageListener(AndroidVideoCapture owner) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader is not null && !owner._disposed)
            {
                try { owner.OnImage(reader); } catch (Java.Lang.IllegalStateException) { }
            }
        }
    }

    private sealed class DeviceCallback(TaskCompletionSource<CameraDevice> opened) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => opened.TrySetResult(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            opened.TrySetException(new IOException("Camera disconnected."));
            camera.Close();
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            opened.TrySetException(new IOException($"Camera error: {error}."));
            camera.Close();
        }
    }

    private sealed class SessionCallback(TaskCompletionSource<CameraCaptureSession> configured) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) => configured.TrySetResult(session);

        public override void OnConfigureFailed(CameraCaptureSession session) =>
            configured.TrySetException(new IOException("Camera session configuration failed (unsupported size/stream combination)."));
    }
}
