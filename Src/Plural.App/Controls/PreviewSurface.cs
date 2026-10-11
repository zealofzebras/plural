using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Plural.Core.Platform;

namespace Plural.App.Controls;

/// <summary>
/// Receives preview frames on the camera thread, copies them into a private buffer and presents them
/// into a WriteableBitmap on the UI thread. Frames are dropped while one is still pending.
/// </summary>
public sealed class PreviewSurface
{
    private readonly object _lock = new();
    private byte[] _buffer = [];
    private int _width, _height, _stride, _rotation;
    private bool _mirror, _pending;
    private IVideoCapture? _capture;
    private WriteableBitmap? _bitmap;

    public WriteableBitmap? Bitmap => _bitmap;
    public int Rotation { get; private set; }
    public bool Mirror { get; private set; }
    public bool HasFrame => _bitmap is not null;

    /// <summary>Raised on the UI thread after a new frame was presented (or the surface was cleared).</summary>
    public event Action? Updated;

    public void Attach(IVideoCapture capture)
    {
        Detach();
        _capture = capture;
        capture.PreviewFrameReady += OnFrame;
    }

    public void Detach()
    {
        if (_capture is { } c)
        {
            c.PreviewFrameReady -= OnFrame;
            _capture = null;
        }
        lock (_lock)
        {
            _width = 0;
            _pending = false;
        }
        _bitmap = null;
        Updated?.Invoke();
    }

    private void OnFrame(PreviewFrame frame)
    {
        var cap = _capture;
        lock (_lock)
        {
            if (_pending)
            {
                return;
            }
            var need = frame.Stride * frame.Height;
            var span = frame.Bgra.Span;
            if (frame.Width <= 0 || frame.Height <= 0 || span.Length < need)
            {
                return;
            }
            if (_buffer.Length < need)
            {
                _buffer = new byte[need];
            }
            span[..need].CopyTo(_buffer);
            _width = frame.Width;
            _height = frame.Height;
            _stride = frame.Stride;
            _rotation = cap?.RotationDegrees ?? 0;
            _mirror = cap?.MirrorPreview ?? false;
            _pending = true;
        }
        Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
    }

    private void Present()
    {
        lock (_lock)
        {
            if (_width == 0 || _capture is null)
            {
                _pending = false;
                return;
            }
            if (_bitmap is null || _bitmap.PixelSize.Width != _width || _bitmap.PixelSize.Height != _height)
            {
                _bitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using (var fb = _bitmap.Lock())
            {
                var rowBytes = Math.Min(Math.Min(_stride, fb.RowBytes), _width * 4);
                for (var y = 0; y < _height; y++)
                {
                    Marshal.Copy(_buffer, y * _stride, fb.Address + y * fb.RowBytes, rowBytes);
                }
            }
            Rotation = ((_rotation % 360) + 360) % 360;
            Mirror = _mirror;
            _pending = false;
        }
        Updated?.Invoke();
    }
}
