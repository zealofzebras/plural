using System.Threading.Channels;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.Opengl;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Plural.Core.Platform;
using Plural.Core.Recording;

namespace Plural.Platform.Android;

/// <summary>
/// Camera2 capture with two outputs in one session: a small YUV ImageReader for the on-screen preview, and a SurfaceTexture
/// that a GL thread draws (rotated upright, never mirrored) into a MediaCodec H.264 input surface.
///
/// Threads: "plural-camera" (Camera2 callbacks, preview reader), "plural-gl" (EGL context, SurfaceTexture frames, encoder
/// creation), one encoder drain thread per encoder, and "plural-dispatch" which raises <see cref="SampleEncoded"/> so a slow
/// consumer can never stall the codec output queue.
/// </summary>
public sealed class AndroidVideoCapture : IVideoCapture
{
    private const string Tag = "Plural";
    private const long PreviewIntervalNs = 1_000_000_000L / 15 - 5_000_000L;
    private const int MaxPreviewWidth = 640;

    private readonly Context _context;
    private readonly CameraManager _manager;
    private readonly VideoSettings _settings;

    private HandlerThread? _cameraThread;
    private Handler? _handler;
    private HandlerThread? _glThread;
    private Handler? _gl;
    private CameraDevice? _device;
    private CameraCaptureSession? _session;
    private ImageReader? _reader;
    private bool _disposed;

    // Camera timestamp -> MediaClock mapping.
    private long _mediaTicksAtOpen;
    private long _cameraNsAtOpen;

    // Encoding state.
    private volatile bool _encoding;
    private volatile bool _waitForKeyFrame;
    private volatile Encoder? _enc;
    private int _lockedRotation;
    private long _startMediaTicks;
    private long _lastPtsNs;
    private int _lockedWidth, _lockedHeight;

    // Dispatch of samples to the consumer.
    private readonly Channel<EncodedSample> _samples = Channel.CreateUnbounded<EncodedSample>(new UnboundedChannelOptions { SingleReader = true });
    private Thread? _dispatchThread;

    // GL state (GL thread only).
    private EGLDisplay? _eglDisplay;
    private EGLContext? _eglContext;
    private EGLConfig? _eglConfig;
    private EGLSurface? _pbuffer;
    private SurfaceTexture? _st;
    private Surface? _stSurface;
    private int _texId;
    private int _program, _aPos, _aTex, _uTex;
    private readonly float[] _texMatrix = new float[16];
    private Java.Nio.FloatBuffer? _vertices;

    // Diagnostics.
    private FrameListener? _frameListener;
    private ImageListener? _imageListener;
    private long _framesIn, _framesDrawn, _framesOut, _lastLogNs, _framesSeen;

    // Preview conversion state.
    private long _lastPreviewNs;
    private byte[] _yBuf = [], _uBuf = [], _vBuf = [];
    private readonly byte[][] _bgra = [[], []];
    private int _bgraIndex;
    private int _camW, _camH;

    public CameraInfo Camera { get; }
    public int FrameRate { get; private set; }

    /// <summary>SENSOR_ORIENTATION of the camera.</summary>
    public int SensorOrientation { get; private set; }

    public int Width => _encoding ? _lockedWidth : EncodeSize(RotationDegrees).W;
    public int Height => _encoding ? _lockedHeight : EncodeSize(RotationDegrees).H;

    /// <summary>Frames are rotated by the GL pipeline, so the encoded video is already upright.</summary>
    public int EncodedRotationDegrees => 0;

    /// <summary>Clockwise degrees to rotate the (sensor-oriented) preview frames so they appear upright for the current device orientation.</summary>
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

    public event Action<PreviewFrame>? PreviewFrameReady;
    public event Action<EncodedSample>? SampleEncoded;

    internal AndroidVideoCapture(Context context, CameraManager manager, CameraInfo camera, VideoSettings settings)
    {
        _context = context;
        _manager = manager;
        _settings = settings;
        Camera = camera;
    }

    private (int W, int H) EncodeSize(int rotation) => rotation % 180 == 0 ? (_camW, _camH) : (_camH, _camW);

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

    // ------------------------------------------------------------------ open

    internal async Task OpenAsync(CancellationToken ct)
    {
        var chars = _manager.GetCameraCharacteristics(Camera.Id);
        var map = CameraHelpers.GetMap(chars) ?? throw new InvalidOperationException("Camera has no stream configurations.");
        SensorOrientation = CameraHelpers.GetInt(chars, CameraCharacteristics.SensorOrientation, 0);

        var size = ChooseSize(map.GetOutputSizes(Java.Lang.Class.FromType(typeof(MediaRecorder))), _settings.Width, _settings.Height);
        _camW = size.Width;
        _camH = size.Height;
        var fpsRange = ChooseFpsRange(chars, _settings.FrameRate);
        FrameRate = fpsRange is { } r ? r.Upper : _settings.FrameRate;
        var previewSize = ChoosePreviewSize(map.GetOutputSizes((int)ImageFormatType.Yuv420888), _camW, _camH);

        _cameraThread = new HandlerThread("plural-camera");
        _cameraThread.Start();
        _handler = new Handler(_cameraThread.Looper!);
        _glThread = new HandlerThread("plural-gl");
        _glThread.Start();
        _gl = new Handler(_glThread.Looper!);

        _dispatchThread = new Thread(DispatchLoop) { IsBackground = true, Name = "plural-dispatch" };
        _dispatchThread.Start();

        _reader = ImageReader.NewInstance(previewSize.Width, previewSize.Height, ImageFormatType.Yuv420888, 2)!;
        _imageListener = new ImageListener(this);
        _reader.SetOnImageAvailableListener(_imageListener, _handler);

        // Sample both clocks together so camera frame timestamps can be mapped to MediaClock.
        var realtime = CameraHelpers.GetInt(chars, CameraCharacteristics.SensorInfoTimestampSource, 0) == 1;
        _mediaTicksAtOpen = MediaClock.Now;
        _cameraNsAtOpen = realtime ? SystemClock.ElapsedRealtimeNanos() : Java.Lang.JavaSystem.NanoTime();
        Log.Info(Tag, $"open camera {Camera.Id} {_camW}x{_camH}@{FrameRate} sensor={SensorOrientation} tsSource={(realtime ? "REALTIME" : "UNKNOWN")} " +
                      $"mediaTicksAtOpen={_mediaTicksAtOpen} camNsAtOpen={_cameraNsAtOpen}");

        // GL: context, camera texture and a pre-warmed encoder for the current orientation.
        OnGl(() =>
        {
            InitGl();
            _st = new SurfaceTexture(_texId);
            _st.SetDefaultBufferSize(_camW, _camH);
            _frameListener = new FrameListener(this);
            _st.SetOnFrameAvailableListener(_frameListener, _gl);
            _stSurface = new Surface(_st);
            EnsureEncoder(EncodeSize(RotationDegrees));
        });

        var opened = new TaskCompletionSource<CameraDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => opened.TrySetCanceled(ct));
        _manager.OpenCamera(Camera.Id, new DeviceCallback(opened), _handler);
        _device = await opened.Task.ConfigureAwait(false);

        var configured = new TaskCompletionSource<CameraCaptureSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg2 = ct.Register(() => configured.TrySetCanceled(ct));
        var surfaces = new List<Surface> { _reader.Surface!, _stSurface! };
#pragma warning disable CA1422 // SessionConfiguration needs API 28; the legacy overload works from API 21.
#pragma warning disable CS0618
        _device.CreateCaptureSession(surfaces, new SessionCallback(configured), _handler);
#pragma warning restore CS0618
#pragma warning restore CA1422
        _session = await configured.Task.ConfigureAwait(false);

        var builder = _device.CreateCaptureRequest(CameraTemplate.Record)!;
        builder.AddTarget(_reader.Surface!);
        builder.AddTarget(_stSurface!);
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
            var score = Math.Abs(hi - fps) * 1000L + (hi - lo) * 10L + (hi < fps ? 1 : 0);
            if (score < bestScore)
            {
                bestScore = score;
                best = (lo, hi);
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ GL thread helpers

    /// <summary>Runs <paramref name="action"/> on the GL thread and waits for it (rethrowing its exception).</summary>
    private void OnGl(Action action)
    {
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        _gl!.Post(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(10_000))
        {
            throw new TimeoutException("GL thread did not respond.");
        }
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private void InitGl()
    {
        _eglDisplay = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
        var version = new int[2];
        if (!EGL14.EglInitialize(_eglDisplay, version, 0, version, 1))
        {
            throw new InvalidOperationException("eglInitialize failed.");
        }
        int[] attribs =
        [
            EGL14.EglRedSize, 8, EGL14.EglGreenSize, 8, EGL14.EglBlueSize, 8, EGL14.EglAlphaSize, 8,
            EGL14.EglRenderableType, EGL14.EglOpenglEs2Bit,
            EGL14.EglSurfaceType, EGL14.EglWindowBit | EGL14.EglPbufferBit,
            EGLExt.EglRecordableAndroid, 1,
            EGL14.EglNone,
        ];
        var configs = new EGLConfig[1];
        var num = new int[1];
        if (!EGL14.EglChooseConfig(_eglDisplay, attribs, 0, configs, 0, 1, num, 0) || num[0] < 1)
        {
            throw new InvalidOperationException("No recordable EGL config.");
        }
        _eglConfig = configs[0];
        _eglContext = EGL14.EglCreateContext(_eglDisplay, _eglConfig, EGL14.EglNoContext, [EGL14.EglContextClientVersion, 2, EGL14.EglNone], 0);
        _pbuffer = EGL14.EglCreatePbufferSurface(_eglDisplay, _eglConfig, [EGL14.EglWidth, 1, EGL14.EglHeight, 1, EGL14.EglNone], 0);
        if (!EGL14.EglMakeCurrent(_eglDisplay, _pbuffer, _pbuffer, _eglContext))
        {
            throw new InvalidOperationException("eglMakeCurrent failed.");
        }

        var tex = new int[1];
        GLES20.GlGenTextures(1, tex, 0);
        _texId = tex[0];
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _texId);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMinFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureMagFilter, GLES20.GlLinear);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapS, GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(GLES11Ext.GlTextureExternalOes, GLES20.GlTextureWrapT, GLES20.GlClampToEdge);

        const string vs = "uniform mat4 uTex;attribute vec4 aPos;attribute vec4 aTex;varying vec2 vTex;" +
                          "void main(){gl_Position=aPos;vTex=(uTex*aTex).xy;}";
        const string fs = "#extension GL_OES_EGL_image_external : require\nprecision mediump float;varying vec2 vTex;" +
                          "uniform samplerExternalOES sTex;void main(){gl_FragColor=texture2D(sTex,vTex);}";
        var v = Compile(GLES20.GlVertexShader, vs);
        var f = Compile(GLES20.GlFragmentShader, fs);
        _program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(_program, v);
        GLES20.GlAttachShader(_program, f);
        GLES20.GlLinkProgram(_program);
        var linked = new int[1];
        GLES20.GlGetProgramiv(_program, GLES20.GlLinkStatus, linked, 0);
        if (linked[0] == 0)
        {
            throw new InvalidOperationException("GL program link failed: " + GLES20.GlGetProgramInfoLog(_program));
        }
        _aPos = GLES20.GlGetAttribLocation(_program, "aPos");
        _aTex = GLES20.GlGetAttribLocation(_program, "aTex");
        _uTex = GLES20.GlGetUniformLocation(_program, "uTex");
        _vertices = Java.Nio.ByteBuffer.AllocateDirect(16 * 4)!.Order(Java.Nio.ByteOrder.NativeOrder()!)!.AsFloatBuffer()!;
        SetQuad(0, _camW, _camH);
        Log.Info(Tag, $"GL ready: {GLES20.GlGetString(GLES20.GlRenderer)} / {GLES20.GlGetString(GLES20.GlVersion)}");
    }

    private static int Compile(int type, string source)
    {
        var s = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(s, source);
        GLES20.GlCompileShader(s);
        var ok = new int[1];
        GLES20.GlGetShaderiv(s, GLES20.GlCompileStatus, ok, 0);
        if (ok[0] == 0)
        {
            throw new InvalidOperationException("GL shader compile failed: " + GLES20.GlGetShaderInfoLog(s));
        }
        return s;
    }

    /// <summary>Quad covering the output after rotating the camera image clockwise by <paramref name="rotation"/>. Interleaved x,y,u,v.</summary>
    private void SetQuad(int rotation, int srcW, int srcH)
    {
        var theta = rotation * Math.PI / 180.0;
        var cos = (float)Math.Cos(theta);
        var sin = (float)Math.Sin(theta);
        var (outW, outH) = rotation % 180 == 0 ? (srcW, srcH) : (srcH, srcW);
        (float X, float Y, float U, float V)[] corners = [(-1, -1, 0, 0), (1, -1, 1, 0), (-1, 1, 0, 1), (1, 1, 1, 1)];
        var data = new float[16];
        for (var i = 0; i < 4; i++)
        {
            var x = corners[i].X * srcW / 2f;
            var y = corners[i].Y * srcH / 2f;
            var rx = x * cos + y * sin;
            var ry = -x * sin + y * cos;
            data[i * 4] = rx / (outW / 2f);
            data[i * 4 + 1] = ry / (outH / 2f);
            data[i * 4 + 2] = corners[i].U;
            data[i * 4 + 3] = corners[i].V;
        }
        _vertices!.Position(0);
        _vertices.Put(data);
        _vertices.Position(0);
    }

    // ------------------------------------------------------------------ encoder

    private sealed class Encoder
    {
        public required MediaCodec Codec { get; init; }
        public required Surface InputSurface { get; init; }
        public required EGLSurface EglSurface { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public Thread? Drain { get; set; }
        public volatile bool Stop;
        public byte[]? Config;
    }

    /// <summary>GL thread: makes sure an encoder of this size exists and is the current render target.</summary>
    private void EnsureEncoder((int W, int H) size)
    {
        if (_enc is { } existing && existing.Width == size.W && existing.Height == size.H)
        {
            return;
        }
        if (_enc is { } old)
        {
            EGL14.EglMakeCurrent(_eglDisplay, _pbuffer, _pbuffer, _eglContext);
            _enc = null;
            ReleaseEncoder(old);
        }

        var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, size.W, size.H);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, _settings.BitRate);
        format.SetInteger(MediaFormat.KeyFrameRate, FrameRate);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 1);
        var codec = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc)!;
        codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        var surface = codec.CreateInputSurface()!;
        codec.Start();
        var egl = EGL14.EglCreateWindowSurface(_eglDisplay, _eglConfig, surface, [EGL14.EglNone], 0);
        var enc = new Encoder { Codec = codec, InputSurface = surface, EglSurface = egl!, Width = size.W, Height = size.H };
        enc.Drain = new Thread(() => DrainLoop(enc)) { IsBackground = true, Name = "plural-h264" };
        enc.Drain.Start();
        EGL14.EglMakeCurrent(_eglDisplay, egl, egl, _eglContext);
        _enc = enc;
        Log.Info(Tag, $"encoder created {size.W}x{size.H} bitrate={_settings.BitRate} fps={FrameRate}");
    }

    private void ReleaseEncoder(Encoder enc)
    {
        enc.Stop = true;
        enc.Drain?.Join(2000);
        try { enc.Codec.Stop(); } catch (Java.Lang.IllegalStateException) { }
        enc.Codec.Release();
        EGL14.EglDestroySurface(_eglDisplay, enc.EglSurface);
        enc.InputSurface.Release();
    }

    public void StartEncoding()
    {
        if (_disposed || _gl is null)
        {
            return;
        }
        var rotation = RotationDegrees;
        var size = EncodeSize(rotation);
        _encoding = false;
        OnGl(() =>
        {
            EnsureEncoder(size);
            SetQuad(rotation, _camW, _camH);
            _lockedRotation = rotation;
            _lockedWidth = size.W;
            _lockedHeight = size.H;
            _lastPtsNs = 0;
            _framesDrawn = _framesOut = 0;
            _waitForKeyFrame = true;
            _startMediaTicks = MediaClock.Now;
            _encoding = true;
            var p = new Bundle();
            p.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0);
            try { _enc!.Codec.SetParameters(p); } catch (Java.Lang.IllegalStateException) { }
        });
        Log.Info(Tag, $"StartEncoding size={size.W}x{size.H} rotation={rotation} mediaNow={_startMediaTicks}");
    }

    /// <summary>Asks the encoder for a sync frame now (a remote viewer joined).</summary>
    public void RequestKeyFrame()
    {
        if (_disposed || _enc is not { } enc)
        {
            return;
        }
        try
        {
            using var p = new Bundle();
            p.PutInt(MediaCodec.ParameterKeyRequestSyncFrame, 0);
            enc.Codec.SetParameters(p);
        }
        catch (Java.Lang.IllegalStateException) { }
    }

    public void StopEncoding()
    {
        if (_encoding)
        {
            Log.Info(Tag, $"StopEncoding framesDrawn={_framesDrawn} samplesOut={_framesOut}");
        }
        _encoding = false;
    }

    private long ToMediaNs(long cameraNs) => (_mediaTicksAtOpen + (cameraNs - _cameraNsAtOpen) / 100L) * 100L;

    /// <summary>GL thread: a new camera frame is available.</summary>
    private void OnFrame()
    {
        if (_disposed || _st is null)
        {
            return;
        }
        try
        {
            _st.UpdateTexImage();
        }
        catch (Java.Lang.RuntimeException ex)
        {
            Log.Warn(Tag, "updateTexImage: " + ex.Message);
            return;
        }
        _framesIn++;
        if (_framesIn <= 3) { Log.Info(Tag, $"updateTexImage ok ts={_st.Timestamp}"); }
        if (_encoding && _enc is { } enc)
        {
            _st.GetTransformMatrix(_texMatrix);
            var cameraNs = _st.Timestamp;
            var ptsNs = cameraNs == 0 ? MediaClock.Now * 100L : ToMediaNs(cameraNs);
            if (ptsNs <= _lastPtsNs)
            {
                Log.Warn(Tag, $"non-monotonic pts {ptsNs} <= {_lastPtsNs}; bumping");
                ptsNs = _lastPtsNs + 1000;
            }
            _lastPtsNs = ptsNs;

            GLES20.GlViewport(0, 0, enc.Width, enc.Height);
            GLES20.GlUseProgram(_program);
            GLES20.GlActiveTexture(GLES20.GlTexture0);
            GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _texId);
            GLES20.GlUniformMatrix4fv(_uTex, 1, false, _texMatrix, 0);
            _vertices!.Position(0);
            GLES20.GlVertexAttribPointer(_aPos, 2, GLES20.GlFloat, false, 16, _vertices);
            GLES20.GlEnableVertexAttribArray(_aPos);
            _vertices.Position(2);
            GLES20.GlVertexAttribPointer(_aTex, 2, GLES20.GlFloat, false, 16, _vertices);
            GLES20.GlEnableVertexAttribArray(_aTex);
            GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);
            EGLExt.EglPresentationTimeANDROID(_eglDisplay, enc.EglSurface, ptsNs);
            EGL14.EglSwapBuffers(_eglDisplay, enc.EglSurface);
            _framesDrawn++;
        }

        var now = Java.Lang.JavaSystem.NanoTime();
        if (now - _lastLogNs >= 1_000_000_000L)
        {
            if (_lastLogNs != 0)
            {
                Log.Debug(Tag, $"camera frames in={_framesIn} drawn={_framesDrawn} encoded out={Interlocked.Read(ref _framesOut)} encoding={_encoding}");
            }
            _framesIn = 0;
            _lastLogNs = now;
        }
    }

    private void DrainLoop(Encoder enc)
    {
        var info = new MediaCodec.BufferInfo();
        var codec = enc.Codec;
        var first = true;
        while (!enc.Stop)
        {
            int index;
            try
            {
                index = codec.DequeueOutputBuffer(info, 50_000);
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, "dequeueOutputBuffer: " + ex.Message);
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
                        enc.Config = cfg;
                    }
                    else if (_encoding && ReferenceEquals(_enc, enc))
                    {
                        var key = flags.HasFlag(MediaCodecBufferFlags.KeyFrame);
                        if (_waitForKeyFrame && !key)
                        {
                            // Discard until the requested sync frame arrives.
                        }
                        else
                        {
                            var cfg = key ? enc.Config : null;
                            var data = new byte[(cfg?.Length ?? 0) + info.Size];
                            cfg?.CopyTo(data, 0);
                            buf.Get(data, cfg?.Length ?? 0, info.Size);
                            if (key)
                            {
                                _waitForKeyFrame = false;
                            }
                            var ticks = info.PresentationTimeUs * 10;
                            if (first)
                            {
                                first = false;
                                Log.Info(Tag, $"first sample pts={ticks} start={_startMediaTicks} delta={(ticks - _startMediaTicks) / 10000.0:F1} ms key={key} cfg={cfg?.Length}");
                            }
                            Interlocked.Increment(ref _framesOut);
                            _samples.Writer.TryWrite(new EncodedSample(data, ticks, key));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, "drain: " + ex.Message);
            }
            finally
            {
                try { codec.ReleaseOutputBuffer(index, false); } catch (Exception) { }
            }
        }
    }

    private void DispatchLoop()
    {
        var reader = _samples.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var sample))
            {
                try
                {
                    SampleEncoded?.Invoke(sample);
                }
                catch (Exception ex)
                {
                    Log.Warn(Tag, "SampleEncoded handler threw: " + ex);
                }
            }
        }
    }

    // ------------------------------------------------------------------ preview

    private void OnImage(ImageReader reader)
    {
        var image = reader.AcquireLatestImage();
        if (image is null)
        {
            return;
        }
        try { ConvertPreview(image); }
        finally { image.Close(); } // Dispose() does not close the Java Image; unclosed images stall the whole capture session.
    }

    private void ConvertPreview(Image image)
    {
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

    // ------------------------------------------------------------------ dispose

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
            // Stop the producers first (session, camera, reader), then GL and the encoder, then the threads.
            try { _session?.StopRepeating(); } catch (Java.Lang.Exception) { }
            try { _session?.Close(); } catch (Java.Lang.Exception) { }
            try { _device?.Close(); } catch (Java.Lang.Exception) { }
            _reader?.SetOnImageAvailableListener(null, null);
            _reader?.Close();

            if (_gl is not null)
            {
                try
                {
                    OnGl(() =>
                    {
                        _st?.SetOnFrameAvailableListener(null);
                        if (_enc is { } enc)
                        {
                            _enc = null;
                            EGL14.EglMakeCurrent(_eglDisplay, _pbuffer, _pbuffer, _eglContext);
                            ReleaseEncoder(enc);
                        }
                        _stSurface?.Release();
                        _st?.Release();
                        if (_eglDisplay is not null)
                        {
                            EGL14.EglMakeCurrent(_eglDisplay, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
                            if (_pbuffer is not null) { EGL14.EglDestroySurface(_eglDisplay, _pbuffer); }
                            if (_eglContext is not null) { EGL14.EglDestroyContext(_eglDisplay, _eglContext); }
                            EGL14.EglTerminate(_eglDisplay);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn(Tag, "GL teardown: " + ex.Message);
                }
            }
            _glThread?.QuitSafely();
            _glThread?.Join();
            _cameraThread?.QuitSafely();
            _cameraThread?.Join();
            _samples.Writer.TryComplete();
            _dispatchThread?.Join(2000);
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ callbacks

    private sealed class FrameListener(AndroidVideoCapture owner) : Java.Lang.Object, SurfaceTexture.IOnFrameAvailableListener
    {
        public void OnFrameAvailable(SurfaceTexture? surfaceTexture)
        {
            if (owner._framesSeen++ < 5) { Log.Info(Tag, $"OnFrameAvailable #{owner._framesSeen} disposed={owner._disposed} st={(owner._st is null)}"); }
            try { owner.OnFrame(); }
            catch (Exception ex) { Log.Warn(Tag, "OnFrame: " + ex); }
        }
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
