using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Plural.Core.Platform;
using Plural.Core.Playback;

namespace Plural.App.ViewModels;

/// <summary>
/// One video track on the Player page: the latest decoded frame (copied from the player's thread into a private buffer and presented into a
/// <see cref="WriteableBitmap"/> on the UI thread), the poster, and the state the stage draws. <see cref="Changed"/> fires on the UI thread.
/// </summary>
public sealed class VideoTileViewModel
{
    private readonly object _lock = new();
    private byte[] _buffer = [];
    private int _width, _height, _stride;
    private bool _pending;
    private bool _disposed;

    public VideoTileViewModel(PlayerTrack track, int index)
    {
        Track = track;
        Index = index;
        Rotation = ((track.RotationDegrees % 360) + 360) % 360;
        Tile = VideoTile.From(track.Width, track.Height, track.RotationDegrees);
        Name = track.Name.Length > 0 ? track.Name : "Video";
        Device = track.Device;
        Offset = track.Offset;
        Refresh();
    }

    public PlayerTrack Track { get; }

    /// <summary>Position among the take's video tracks (layout order).</summary>
    public int Index { get; }

    public VideoTile Tile { get; }
    public string Name { get; }
    public string Device { get; }
    public TimeSpan Offset { get; }

    /// <summary>Clockwise degrees; recorded video is never mirrored.</summary>
    public int Rotation { get; }

    public WriteableBitmap? Frame { get; private set; }
    public Bitmap? Poster { get; set; }

    /// <summary>The take's poster belongs to the first video track only.</summary>
    public bool PosterAllowed { get; set; }

    public bool HasFrame => Frame is not null;

    /// <summary>The playhead is before this track's first frame: show the poster / black, not the last frame of an earlier seek.</summary>
    public bool BeforeStart { get; private set; }

    /// <summary>This device cannot show the video (see <see cref="Problem"/>); only shown while there is no frame.</summary>
    public bool Unavailable { get; private set; }

    public string? Problem { get; private set; }

    public event Action? Changed;

    /// <summary>Re-reads availability from the track (after <see cref="TakePlayer.VideoUnavailable"/>).</summary>
    public void Refresh()
    {
        Unavailable = !Track.CanPlay;
        Problem = Track.Problem;
        Changed?.Invoke();
    }

    public void SetBeforeStart(bool value)
    {
        if (BeforeStart == value) { return; }
        BeforeStart = value;
        Changed?.Invoke();
    }

    /// <summary>Copies the frame (valid only during the call) and presents it on the UI thread. Any thread.</summary>
    public void Push(PreviewFrame frame) => Push(frame.Bgra.Span, frame.Width, frame.Height, frame.Stride);

    /// <summary>Releases the frame and poster bitmaps (page left); later pushes are ignored. UI thread.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _buffer = [];
            Frame?.Dispose();
            Frame = null;
        }
        Poster?.Dispose();
        Poster = null;
    }

    public void Push(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        lock (_lock)
        {
            var need = stride * height;
            if (_disposed) { return; }
            if (width <= 0 || height <= 0 || bgra.Length < need) { return; }
            if (_buffer.Length < need) { _buffer = new byte[need]; }
            bgra[..need].CopyTo(_buffer);
            _width = width;
            _height = height;
            _stride = stride;
            if (_pending) { return; }
            _pending = true;
        }
        Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
    }

    private void Present()
    {
        lock (_lock)
        {
            _pending = false;
            if (_width == 0 || _disposed) { return; }
            if (Frame is null || Frame.PixelSize.Width != _width || Frame.PixelSize.Height != _height)
            {
                Frame = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using var fb = Frame.Lock();
            var rowBytes = Math.Min(Math.Min(_stride, fb.RowBytes), _width * 4);
            for (var y = 0; y < _height; y++)
            {
                Marshal.Copy(_buffer, y * _stride, fb.Address + y * fb.RowBytes, rowBytes);
            }
        }
        Changed?.Invoke();
    }
}
