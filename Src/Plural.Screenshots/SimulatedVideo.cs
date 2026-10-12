using Plural.Core.Platform;
using Plural.Core.Recording;

namespace Plural.Screenshots;

/// <summary>
/// Camera for the --mixer preview only: a moving test scene as preview frames and placeholder H.264 access units while encoding
/// (enough for the recorder's containers; the video is not meant to be watched).
/// </summary>
internal sealed class SimulatedVideo : IVideoPlatform
{
    private static readonly CameraInfo Front = new("front", "Front camera", true);
    private static readonly CameraInfo Back = new("back", "Back camera", false);

    public IReadOnlyList<CameraInfo> GetCameras() => [Back, Front];

    public Task<IVideoCapture> OpenAsync(CameraInfo camera, VideoSettings settings, CancellationToken ct = default) =>
        Task.FromResult<IVideoCapture>(new Capture(camera));

    private sealed class Capture : IVideoCapture
    {
        private const int W = 640, H = 360;
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _encoding;
        private bool _keySent;

        public Capture(CameraInfo camera)
        {
            Camera = camera;
            _ = Task.Run(RunAsync);
        }

        public CameraInfo Camera { get; }
        public int Width => 1920;
        public int Height => 1080;
        public int FrameRate => 30;
        public event Action<PreviewFrame>? PreviewFrameReady;
        public event Action<EncodedSample>? SampleEncoded;

        public void StartEncoding()
        {
            _keySent = false;
            _encoding = true;
        }

        public void StopEncoding() => _encoding = false;

        private async Task RunAsync()
        {
            var frame = new byte[W * H * 4];
            for (var n = 0; !_stop.IsCancellationRequested; n++)
            {
                Draw(frame, n);
                PreviewFrameReady?.Invoke(new PreviewFrame(frame.ToArray(), W, H, W * 4));
                if (_encoding)
                {
                    byte[] key = [0, 0, 0, 1, 0x67, 0x42, 0xC0, 0x1F, 0xDA, 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80, 0, 0, 1, 0x65, 1, 2, 3];
                    byte[] delta = [0, 0, 1, 0x41, 9, 9, 9];
                    SampleEncoded?.Invoke(new EncodedSample(_keySent ? delta : key, MediaClock.Now, !_keySent));
                    _keySent = true;
                }
                try { await Task.Delay(66, _stop.Token); } catch (OperationCanceledException) { }
            }
        }

        /// <summary>Studio-ish backdrop: vertical gradient, soft light that drifts, a "speaker" silhouette.</summary>
        private static void Draw(byte[] px, int n)
        {
            var lx = W * (0.5 + 0.3 * Math.Sin(n / 30.0));
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    var t = (double)y / H;
                    var d = Math.Sqrt((x - lx) * (x - lx) + (y - 90) * (y - 90)) / W;
                    var light = Math.Max(0, 0.55 - d) * 1.2;
                    var b = 70 + 60 * (1 - t) + 120 * light;
                    var g = 40 + 30 * (1 - t) + 100 * light;
                    var r = 30 + 20 * (1 - t) + 140 * light;
                    // Head and shoulders.
                    var hx = x - W / 2.0; var hy = y - 170.0;
                    var shoulders = y > 250 && Math.Abs(hx) < 150 - (360 - y) * 0.6;
                    if (hx * hx / (55.0 * 55) + hy * hy / (68.0 * 68) < 1 || shoulders)
                    {
                        r = 25; g = 22; b = 28;
                    }
                    var i = (y * W + x) * 4;
                    px[i] = (byte)Math.Min(255, b); px[i + 1] = (byte)Math.Min(255, g); px[i + 2] = (byte)Math.Min(255, r); px[i + 3] = 255;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            return ValueTask.CompletedTask;
        }
    }
}
