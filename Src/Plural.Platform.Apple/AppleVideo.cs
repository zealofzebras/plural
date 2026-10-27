using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Platform;
using Plural.Core.Recording;

namespace Plural.Platform.Apple;

/// <summary>
/// Cameras through AVFoundation (Objective-C runtime) with VideoToolbox H.264 encoding, shared by macOS and iOS.
/// macOS lists every video device (built-in, USB/external webcams, Continuity Camera, Desk View); iOS offers the main
/// (back wide-angle) and the front camera.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed unsafe class AppleVideoPlatform : IVideoPlatform
{
    private readonly Func<int>? _interfaceOrientation;

    /// <param name="interfaceOrientation">
    /// iOS: the current UIInterfaceOrientation (1 portrait, 2 upside down, 3 landscape right, 4 landscape left; 0 unknown), so frames
    /// are captured upright. Null on macOS.
    /// </param>
    public AppleVideoPlatform(Func<int>? interfaceOrientation = null)
    {
        _interfaceOrientation = interfaceOrientation;
    }

    public IReadOnlyList<CameraInfo> GetCameras()
    {
        Av.Load();
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            return OperatingSystem.IsIOS() ? IosCameras() : MacCameras();
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private static List<CameraInfo> IosCameras()
    {
        var list = new List<CameraInfo>();
        var wide = Av.Constant("AVCaptureDeviceTypeBuiltInWideAngleCamera");
        foreach (var (position, name) in new[] { (Av.PositionBack, "Main camera"), (Av.PositionFront, "Front camera") })
        {
            var device = ObjC.Send(ObjC.Class("AVCaptureDevice"), ObjC.Sel("defaultDeviceWithDeviceType:mediaType:position:"), wide, Av.MediaTypeVideo,
                (nint)position);
            if (device != IntPtr.Zero && ObjC.ToManaged(ObjC.Send(device, ObjC.Sel("uniqueID"))) is { } id)
            {
                list.Add(new CameraInfo(id, name, position == Av.PositionFront));
            }
        }
        return list;
    }

    private static List<CameraInfo> MacCameras()
    {
        var types = new List<IntPtr>();
        foreach (var name in new[]
        {
            "AVCaptureDeviceTypeBuiltInWideAngleCamera",
            "AVCaptureDeviceTypeExternal",          // macOS 14+ (USB/Thunderbolt webcams, capture cards)
            "AVCaptureDeviceTypeContinuityCamera",  // macOS 14+ (iPhone as webcam)
            "AVCaptureDeviceTypeDeskViewCamera",    // macOS 13+
        })
        {
            if (Av.Constant(name) is var t && t != IntPtr.Zero)
            {
                types.Add(t);
            }
        }
        if (Av.Constant("AVCaptureDeviceTypeExternal") == IntPtr.Zero && Av.Constant("AVCaptureDeviceTypeExternalUnknown") is var legacy
            && legacy != IntPtr.Zero)
        {
            types.Add(legacy); // macOS 12/13 name for external cameras
        }

        IntPtr devices;
        var discovery = ObjC.Class("AVCaptureDeviceDiscoverySession");
        if (discovery != IntPtr.Zero && types.Count > 0)
        {
            IntPtr array;
            var items = types.ToArray();
            fixed (IntPtr* p = items)
            {
                array = ObjC.Send(ObjC.Class("NSArray"), ObjC.Sel("arrayWithObjects:count:"), p, (nuint)items.Length);
            }
            var session = ObjC.Send(discovery, ObjC.Sel("discoverySessionWithDeviceTypes:mediaType:position:"), array, Av.MediaTypeVideo,
                (nint)Av.PositionUnspecified);
            devices = ObjC.Send(session, ObjC.Sel("devices"));
        }
        else
        {
            devices = ObjC.Send(ObjC.Class("AVCaptureDevice"), ObjC.Sel("devicesWithMediaType:"), Av.MediaTypeVideo);
        }

        var list = new List<CameraInfo>();
        var count = devices == IntPtr.Zero ? 0 : ObjC.SendNUInt(devices, ObjC.Sel("count"));
        for (nuint i = 0; i < count; i++)
        {
            var device = ObjC.Send(devices, ObjC.Sel("objectAtIndex:"), i);
            var id = ObjC.ToManaged(ObjC.Send(device, ObjC.Sel("uniqueID")));
            if (id is null || list.Any(c => c.Id == id))
            {
                continue;
            }
            var name = ObjC.ToManaged(ObjC.Send(device, ObjC.Sel("localizedName"))) ?? "Camera";
            var front = ObjC.SendNInt(device, ObjC.Sel("position")) == Av.PositionFront;
            list.Add(new CameraInfo(id, name, front));
        }
        return list;
    }

    public Task<IVideoCapture> OpenAsync(CameraInfo camera, VideoSettings settings, CancellationToken ct = default) =>
        Task.Run<IVideoCapture>(() =>
        {
            ct.ThrowIfCancellationRequested();
            return AppleVideoCapture.Open(camera, settings, _interfaceOrientation);
        }, ct);
}

/// <summary>Camera permission (AVCaptureDevice authorization) for hosts without Objective-C bindings (macOS).</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public static unsafe class AppleCameraAccess
{
    private const int NotDetermined = 0;
    private const int Authorized = 3;
    private const int BlockIsGlobal = 1 << 28;

    private static readonly object Gate = new();
    private static TaskCompletionSource<bool>? _pending;
    private static IntPtr _block;

    /// <summary>True when the app may use cameras; asks the user (system prompt) the first time.</summary>
    public static Task<bool> EnsureAsync()
    {
        Av.Load();
        var status = (int)ObjC.SendNInt(ObjC.Class("AVCaptureDevice"), ObjC.Sel("authorizationStatusForMediaType:"), Av.MediaTypeVideo);
        if (status == Authorized)
        {
            return Task.FromResult(true);
        }
        if (status != NotDetermined)
        {
            return Task.FromResult(false);
        }
        lock (Gate)
        {
            if (_pending is { } p)
            {
                return p.Task;
            }
            var tcs = _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ObjC.Send(ObjC.Class("AVCaptureDevice"), ObjC.Sel("requestAccessForMediaType:completionHandler:"), Av.MediaTypeVideo, CompletionBlock());
            return tcs.Task;
        }
    }

    /// <summary>A global (never copied or freed) block literal for <c>void (^)(BOOL granted)</c>.</summary>
    private static IntPtr CompletionBlock()
    {
        if (_block != IntPtr.Zero)
        {
            return _block;
        }
        var descriptor = (nuint*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(nuint)));
        descriptor[1] = (nuint)(4 * IntPtr.Size);                     // sizeof(Block_literal)
        var block = (IntPtr*)NativeMemory.AllocZeroed((nuint)(4 * IntPtr.Size));
        block[0] = Exports.Address("/usr/lib/libSystem.B.dylib", "_NSConcreteGlobalBlock"); // isa
        ((int*)(block + 1))[0] = BlockIsGlobal;                          // flags; reserved = 0
        block[2] = (IntPtr)(delegate* unmanaged<IntPtr, byte, void>)&Completed; // invoke
        block[3] = (IntPtr)descriptor;
        return _block = (IntPtr)block;
    }

    [UnmanagedCallersOnly]
    private static void Completed(IntPtr block, byte granted)
    {
        TaskCompletionSource<bool>? p;
        lock (Gate)
        {
            p = _pending;
            _pending = null;
        }
        p?.TrySetResult(granted != 0);
    }
}

/// <summary>An open AVFoundation camera. See <see cref="IVideoCapture"/>.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed unsafe class AppleVideoCapture : IVideoCapture
{
    private const int MaxPreviewSize = 640;
    private static readonly ConcurrentDictionary<IntPtr, AppleVideoCapture> ByDelegate = new();
    private static IntPtr _delegateClass;

    private readonly VideoSettings _settings;
    private readonly Func<int>? _interfaceOrientation;
    private readonly object _encGate = new();
    private IntPtr _session, _input, _output, _connection, _delegate, _queue;
    private VtH264Encoder? _encoder;
    private volatile bool _encoding;
    private volatile bool _forceKey;
    private volatile bool _disposed;

    // frame geometry (capture queue writes, other threads read)
    private volatile int _frameW, _frameH;
    private int _presetW, _presetH;
    private int _lockedW, _lockedH;
    private volatile int _bufferOrientation;   // AVCaptureVideoOrientation currently set on the connection (iOS), 0 = none

    // timestamps
    private bool _tsInitialized, _tsHost;
    private long _hostOffset, _tsOffset = long.MaxValue, _lastTs = long.MinValue;

    // preview
    private readonly byte[][] _preview = new byte[2][];
    private int _previewIndex;
    private long _nextPreview;

    public CameraInfo Camera { get; }
    public int FrameRate { get; private set; }

    public int Width => _encoding ? _lockedW : CurrentSize().W;
    public int Height => _encoding ? _lockedH : CurrentSize().H;

    /// <summary>Frames are captured upright (AVCaptureConnection orientation), so files need no rotation hint.</summary>
    public int EncodedRotationDegrees => 0;

    /// <summary>iOS: rotation of the (upright-when-captured) frames for the current interface orientation; 0 on macOS.</summary>
    public int RotationDegrees
    {
        get
        {
            var buffer = _bufferOrientation;
            var ui = DesiredOrientation();
            if (buffer == 0 || ui == 0 || buffer == ui)
            {
                return 0;
            }
            var delta = OrientationDegrees(buffer) - OrientationDegrees(ui);
            return ((Camera.IsFrontFacing ? -delta : delta) % 360 + 360) % 360;
        }
    }

    public bool MirrorPreview => Camera.IsFrontFacing;

    public event Action<PreviewFrame>? PreviewFrameReady;
    public event Action<EncodedSample>? SampleEncoded;

    private AppleVideoCapture(CameraInfo camera, VideoSettings settings, Func<int>? interfaceOrientation)
    {
        Camera = camera;
        _settings = settings;
        _interfaceOrientation = interfaceOrientation;
        FrameRate = settings.FrameRate > 0 ? settings.FrameRate : 30;
    }

    internal static AppleVideoCapture Open(CameraInfo camera, VideoSettings settings, Func<int>? interfaceOrientation)
    {
        Av.Load();
        var capture = new AppleVideoCapture(camera, settings, interfaceOrientation);
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            capture.Configure();
            return capture;
        }
        catch
        {
            capture.Teardown();
            throw;
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private void Configure()
    {
        var device = ObjC.Send(ObjC.Class("AVCaptureDevice"), ObjC.Sel("deviceWithUniqueID:"), Ns(Camera.Id));
        if (device == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Camera not found: {Camera.Name}");
        }
        IntPtr error = IntPtr.Zero;
        var input = ObjC.Send(ObjC.Class("AVCaptureDeviceInput"), ObjC.Sel("deviceInputWithDevice:error:"), device, &error);
        if (input == IntPtr.Zero)
        {
            var status = ObjC.SendNInt(ObjC.Class("AVCaptureDevice"), ObjC.Sel("authorizationStatusForMediaType:"), Av.MediaTypeVideo);
            var text = ObjC.ErrorText(error) ?? "The camera could not be opened.";
            throw status is 1 or 2
                ? new UnauthorizedAccessException("Camera access is not allowed. Allow Plural in System Settings › Privacy & Security › Camera.")
                : new InvalidOperationException(text);
        }
        _input = ObjC.Retain(input);

        _session = ObjC.Send(ObjC.Send(ObjC.Class("AVCaptureSession"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        if (ObjC.RespondsTo(_session, "setAutomaticallyConfiguresApplicationAudioSession:"))
        {
            // iOS: video only; leave the app's audio session (mics, monitoring) alone.
            ObjC.Send(_session, ObjC.Sel("setAutomaticallyConfiguresApplicationAudioSession:"), (byte)0);
        }
        ObjC.Send(_session, ObjC.Sel("beginConfiguration"));
        if (!ObjC.SendBool(_session, ObjC.Sel("canAddInput:"), _input))
        {
            throw new InvalidOperationException($"{Camera.Name} cannot be used (in use by another app?).");
        }
        ObjC.Send(_session, ObjC.Sel("addInput:"), _input);
        ChoosePreset();

        _output = ObjC.Send(ObjC.Send(ObjC.Class("AVCaptureVideoDataOutput"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.Send(_output, ObjC.Sel("setAlwaysDiscardsLateVideoFrames:"), (byte)1);
        var videoSettings = CV.BgraAttributes();
        ObjC.Send(_output, ObjC.Sel("setVideoSettings:"), videoSettings);
        CF.CFRelease(videoSettings);
        _queue = Dispatch.dispatch_queue_create("net.getplural.camera", IntPtr.Zero);
        _delegate = CreateDelegate();
        ByDelegate[_delegate] = this;
        ObjC.Send(_output, ObjC.Sel("setSampleBufferDelegate:queue:"), _delegate, _queue);
        if (!ObjC.SendBool(_session, ObjC.Sel("canAddOutput:"), _output))
        {
            throw new InvalidOperationException($"{Camera.Name} cannot deliver video frames.");
        }
        ObjC.Send(_session, ObjC.Sel("addOutput:"), _output);

        _connection = ObjC.Retain(ObjC.Send(_output, ObjC.Sel("connectionWithMediaType:"), Av.MediaTypeVideo));
        if (_connection != IntPtr.Zero)
        {
            // Record what the camera sees (unmirrored); the preview mirrors front cameras itself.
            if (ObjC.SendBool(_connection, ObjC.Sel("isVideoMirroringSupported")))
            {
                ObjC.Send(_connection, ObjC.Sel("setAutomaticallyAdjustsVideoMirroring:"), (byte)0);
                ObjC.Send(_connection, ObjC.Sel("setVideoMirrored:"), (byte)0);
            }
            UpdateOrientation();
        }
        ObjC.Send(_session, ObjC.Sel("commitConfiguration"));
        SetFrameRate(device);
        ObjC.Send(_session, ObjC.Sel("startRunning"));
    }

    /// <summary>The largest session preset not above the requested size (the UI asks 720p, 1080p or 4K at 16:9).</summary>
    private void ChoosePreset()
    {
        (string Name, int W, int H)[] presets =
        [
            ("AVCaptureSessionPreset3840x2160", 3840, 2160),
            ("AVCaptureSessionPreset1920x1080", 1920, 1080),
            ("AVCaptureSessionPreset1280x720", 1280, 720),
            ("AVCaptureSessionPreset640x480", 640, 480),
        ];
        var wantH = _settings.Height > 0 ? _settings.Height : 1080;
        foreach (var (name, w, h) in presets)
        {
            if (h > wantH)
            {
                continue;
            }
            var preset = Av.Constant(name);
            if (preset != IntPtr.Zero && ObjC.SendBool(_session, ObjC.Sel("canSetSessionPreset:"), preset))
            {
                ObjC.Send(_session, ObjC.Sel("setSessionPreset:"), preset);
                (_presetW, _presetH) = (w, h);
                return;
            }
        }
        // Keep the default preset (High); the size is known from the first frame.
        (_presetW, _presetH) = (_settings.Width > 0 ? _settings.Width : 1920, wantH);
    }

    /// <summary>Applies the requested frame rate when the active format supports it (setting an unsupported one raises an exception).</summary>
    private void SetFrameRate(IntPtr device)
    {
        var fps = FrameRate;
        var ranges = ObjC.Send(ObjC.Send(device, ObjC.Sel("activeFormat")), ObjC.Sel("videoSupportedFrameRateRanges"));
        var count = ranges == IntPtr.Zero ? 0 : ObjC.SendNUInt(ranges, ObjC.Sel("count"));
        var match = IntPtr.Zero;
        var best = 0.0;
        for (nuint i = 0; i < count; i++)
        {
            var range = ObjC.Send(ranges, ObjC.Sel("objectAtIndex:"), i);
            var min = ObjC.SendDouble(range, ObjC.Sel("minFrameRate"));
            var max = ObjC.SendDouble(range, ObjC.Sel("maxFrameRate"));
            if (match == IntPtr.Zero && fps >= min - 0.01 && fps <= max + 0.01)
            {
                match = range;
            }
            best = Math.Max(best, max);
        }
        if (match == IntPtr.Zero)
        {
            if (best >= 1)
            {
                FrameRate = (int)Math.Min(fps, Math.Round(best));
            }
            return;
        }
        // UVC webcams report ranges like exactly 333333/10000000 s (30.00003 fps): 1/30 s would fall outside and raise,
        // so clamp to the range's own durations.
        var duration = CMTime.FromSeconds(1, fps);
        var shortest = ObjC.SendCMTime(match, ObjC.Sel("minFrameDuration"));
        var longest = ObjC.SendCMTime(match, ObjC.Sel("maxFrameDuration"));
        if (!shortest.IsValid || !longest.IsValid)
        {
            return;
        }
        if (duration.Seconds <= shortest.Seconds)
        {
            duration = shortest;
        }
        else if (duration.Seconds >= longest.Seconds)
        {
            duration = longest;
        }
        IntPtr error = IntPtr.Zero;
        if (ObjC.SendBool(device, ObjC.Sel("lockForConfiguration:"), &error))
        {
            ObjC.Send(device, ObjC.Sel("setActiveVideoMinFrameDuration:"), duration);
            ObjC.Send(device, ObjC.Sel("setActiveVideoMaxFrameDuration:"), duration);
            ObjC.Send(device, ObjC.Sel("unlockForConfiguration"));
        }
    }

    private int DesiredOrientation()
    {
        if (_interfaceOrientation is null)
        {
            return 0;
        }
        try
        {
            var o = _interfaceOrientation();
            return o is >= 1 and <= 4 ? o : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>AVCaptureVideoOrientation/UIInterfaceOrientation → how far the UI is turned from portrait.</summary>
    private static int OrientationDegrees(int orientation) => orientation switch
    {
        2 => 180,
        3 => 90,
        4 => 270,
        _ => 0,
    };

    /// <summary>iOS, while not recording: keeps the connection's orientation on the interface's, so frames arrive upright.</summary>
    private void UpdateOrientation()
    {
        var want = DesiredOrientation();
        if (want == 0 || want == _bufferOrientation || _connection == IntPtr.Zero
            || !ObjC.SendBool(_connection, ObjC.Sel("isVideoOrientationSupported")))
        {
            return;
        }
        ObjC.Send(_connection, ObjC.Sel("setVideoOrientation:"), (IntPtr)want);
        _bufferOrientation = want;
    }

    private (int W, int H) CurrentSize()
    {
        if (_frameW > 0 && _frameH > 0)
        {
            return (_frameW, _frameH);
        }
        var portrait = _bufferOrientation is 1 or 2;
        return portrait ? (_presetH, _presetW) : (_presetW, _presetH);
    }

    public void StartEncoding()
    {
        if (_disposed)
        {
            return;
        }
        lock (_encGate)
        {
            _encoding = false;
            var (w, h) = CurrentSize();
            _encoder?.Dispose();
            _encoder = null;
            try
            {
                _encoder = new VtH264Encoder(w, h, FrameRate, _settings.BitRate, RaiseSample);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("H.264 encoder: " + ex.Message);
                throw;
            }
            (_lockedW, _lockedH) = (w, h);
            _forceKey = true;
            _encoding = true;
        }
    }

    public void StopEncoding()
    {
        VtH264Encoder? encoder;
        lock (_encGate)
        {
            _encoding = false;
            encoder = _encoder;
            _encoder = null;
        }
        encoder?.Dispose();
    }

    public void RequestKeyFrame() => _forceKey = true;

    private void RaiseSample(EncodedSample sample)
    {
        try
        {
            SampleEncoded?.Invoke(sample);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("SampleEncoded handler failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- capture queue

    private static IntPtr CreateDelegate()
    {
        lock (ByDelegate)
        {
            if (_delegateClass == IntPtr.Zero)
            {
                var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), "PluralVideoOutputDelegate", 0);
                if (cls == IntPtr.Zero)
                {
                    cls = ObjC.Class("PluralVideoOutputDelegate");
                }
                else
                {
                    ObjC.class_addMethod(cls, ObjC.Sel("captureOutput:didOutputSampleBuffer:fromConnection:"),
                        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnSampleBuffer, "v@:@^v@");
                    ObjC.objc_registerClassPair(cls);
                }
                _delegateClass = cls;
            }
        }
        return ObjC.Send(ObjC.Send(_delegateClass, ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    [UnmanagedCallersOnly]
    private static void OnSampleBuffer(IntPtr self, IntPtr cmd, IntPtr output, IntPtr sampleBuffer, IntPtr connection)
    {
        try
        {
            if (ByDelegate.TryGetValue(self, out var capture) && !capture._disposed)
            {
                capture.OnFrame(sampleBuffer);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Camera frame failed: " + ex.Message);
        }
    }

    private void OnFrame(IntPtr sampleBuffer)
    {
        var image = CM.CMSampleBufferGetImageBuffer(sampleBuffer);
        if (image == IntPtr.Zero)
        {
            return;
        }
        var w = (int)CV.CVPixelBufferGetWidth(image);
        var h = (int)CV.CVPixelBufferGetHeight(image);
        _frameW = w;
        _frameH = h;
        var ts = MapTimestamp(CM.CMSampleBufferGetPresentationTimeStamp(sampleBuffer));

        if (!_encoding)
        {
            UpdateOrientation();
        }
        else
        {
            lock (_encGate)
            {
                if (_encoding && _encoder is { } stale && (stale.Width != w || stale.Height != h) && stale.Frames == 0)
                {
                    // Recording started before the first frame told the real size.
                    stale.Dispose();
                    _encoder = null;
                    _encoder = new VtH264Encoder(w, h, FrameRate, _settings.BitRate, RaiseSample);
                    (_lockedW, _lockedH) = (w, h);
                    _forceKey = true;
                }
                // Later frames of another size (an orientation change in flight) are skipped; the encoder keeps the take's size.
                if (_encoding && _encoder is { } enc && enc.Width == w && enc.Height == h)
                {
                    var key = _forceKey;
                    _forceKey = false;
                    enc.Encode(image, ts, key);
                }
            }
        }

        EmitPreview(image, w, h);
    }

    /// <summary>
    /// Capture timestamps are host time (mach absolute time), the clock Stopwatch/MediaClock also reads on Apple platforms; an offset
    /// measured at the first frame maps them exactly. If they turn out to be on another clock (far from now), the offset is tracked as the
    /// minimum of (now − timestamp) instead, which removes scheduling jitter.
    /// </summary>
    private long MapTimestamp(CMTime pts)
    {
        var now = MediaClock.Now;
        if (!pts.IsValid)
        {
            return Monotonic(now);
        }
        var raw = pts.ToTicks();
        if (!_tsInitialized)
        {
            _tsInitialized = true;
            _hostOffset = now - CM.HostTicks();
            _tsHost = Math.Abs(raw + _hostOffset - now) < 50_000_000L;
        }
        long ts;
        if (_tsHost)
        {
            ts = raw + _hostOffset;
        }
        else
        {
            _tsOffset = Math.Min(_tsOffset, now - raw);
            ts = raw + _tsOffset;
        }
        return Monotonic(ts);
    }

    private long Monotonic(long ts)
    {
        if (ts <= _lastTs)
        {
            ts = _lastTs + 1;
        }
        _lastTs = ts;
        return ts;
    }

    private void EmitPreview(IntPtr image, int w, int h)
    {
        var handler = PreviewFrameReady;
        if (handler is null || w <= 0 || h <= 0)
        {
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var interval = Stopwatch.Frequency / 15;
        if (now < _nextPreview)
        {
            return;
        }
        _nextPreview = _nextPreview == 0 || now - _nextPreview > interval ? now + interval : _nextPreview + interval;

        var scale = Math.Min(1.0, (double)MaxPreviewSize / Math.Max(w, h));
        var pw = Math.Max(2, (int)(w * scale) & ~1);
        var ph = Math.Max(2, (int)(h * scale) & ~1);
        var dst = _preview[_previewIndex];
        if (dst is null || dst.Length != pw * ph * 4)
        {
            dst = _preview[_previewIndex] = new byte[pw * ph * 4];
        }
        _previewIndex ^= 1;

        if (CV.CVPixelBufferGetPixelFormatType(image) != CV.PixelFormatBgra
            || CV.CVPixelBufferLockBaseAddress(image, CV.LockReadOnly) != 0)
        {
            return;
        }
        try
        {
            var src = (byte*)CV.CVPixelBufferGetBaseAddress(image);
            if (src == null)
            {
                return;
            }
            CV.ScaleBgra(src, (int)CV.CVPixelBufferGetBytesPerRow(image), w, h, dst, pw, ph);
        }
        finally
        {
            CV.CVPixelBufferUnlockBaseAddress(image, CV.LockReadOnly);
        }
        handler(new PreviewFrame(dst, pw, ph, pw * 4));
    }

    // ---------------------------------------------------------------- teardown

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        return new ValueTask(Task.Run(Teardown));
    }

    private void Teardown()
    {
        _disposed = true;
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            if (_output != IntPtr.Zero)
            {
                ObjC.Send(_output, ObjC.Sel("setSampleBufferDelegate:queue:"), IntPtr.Zero, IntPtr.Zero);
            }
            if (_session != IntPtr.Zero && ObjC.SendBool(_session, ObjC.Sel("isRunning")))
            {
                ObjC.Send(_session, ObjC.Sel("stopRunning"));
            }
            if (_queue != IntPtr.Zero)
            {
                Dispatch.Drain(_queue); // no frame callback is running or queued after this
            }
            StopEncoding();
            if (_delegate != IntPtr.Zero)
            {
                ByDelegate.TryRemove(_delegate, out _);
            }
            ObjC.Release(_connection);
            ObjC.Release(_output);
            ObjC.Release(_input);
            ObjC.Release(_session);
            ObjC.Release(_delegate);
            if (_queue != IntPtr.Zero)
            {
                Dispatch.dispatch_release(_queue);
            }
            _connection = _output = _input = _session = _delegate = _queue = IntPtr.Zero;
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>Autoreleased NSString (call inside an autorelease pool).</summary>
    private static IntPtr Ns(string s)
    {
        var cf = CF.CreateString(s);
        return ObjC.Send(cf, ObjC.Sel("autorelease"));
    }
}

/// <summary>AVFoundation loading and constants.</summary>
internal static class Av
{
    public const string Lib = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
    public const int PositionUnspecified = 0;
    public const int PositionBack = 1;
    public const int PositionFront = 2;

    private static readonly Lazy<IntPtr> VideoType = new(() => Constant("AVMediaTypeVideo"));

    /// <summary>Loads AVFoundation (and CoreMedia/VideoToolbox) so their Objective-C classes are registered.</summary>
    public static void Load()
    {
        Exports.Library(Lib);
        Exports.Library(CM.Lib);
        Exports.Library(VT.Lib);
    }

    public static IntPtr MediaTypeVideo => VideoType.Value;

    public static IntPtr Constant(string name)
    {
        Load();
        return Exports.Constant(Lib, name);
    }
}
