using Android.Media;
using Plural.Core.Platform;

namespace Plural.Platform.Android;

/// <summary>H.264 playback decoding via MediaCodec.</summary>
public sealed class AndroidVideoDecoderFactory : IVideoDecoderFactory
{
    public bool CanDecode(string codec) =>
        string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase) && AndroidAudioDecoderFactory.HasDecoder(MediaFormat.MimetypeVideoAvc);

    public IVideoDecoder Create(string codec, ReadOnlyMemory<byte> codecPrivate, int width, int height, int maxSize)
    {
        if (!CanDecode(codec))
        {
            throw new NotSupportedException($"No {codec} decoder on this device.");
        }
        return new AndroidVideoDecoder(codecPrivate, width, height, maxSize);
    }
}

/// <summary>
/// Synchronous MediaCodec H.264 decoder with ByteBuffer output (COLOR_FormatYUV420Flexible read through <c>GetOutputImage</c>),
/// converted to BGRA and scaled (nearest neighbour) so the longer side is at most the requested size.
/// Output frames use a ring of reusable buffers: a frame's pixels stay valid for the next <see cref="RingSize"/> frames.
/// </summary>
public sealed class AndroidVideoDecoder : IVideoDecoder
{
    public const int RingSize = 12;
    private const int ColorFormatYuv420Flexible = 0x7F420888;
    private const int ColorFormatYuv420Planar = 19;
    private const int ColorFormatYuv420SemiPlanar = 21;

    private readonly MediaCodec _codec;
    private readonly MediaCodec.BufferInfo _info = new();
    private readonly int _nalLengthSize;
    private readonly int _maxSize;
    private readonly bool _bt709;
    private byte[] _input = new byte[1 << 16];
    private readonly byte[][] _plane = [[], [], []]; // Y, U, V (or the whole buffer at [0] for the raw fallback)
    private readonly byte[][] _ring = new byte[RingSize][];
    private int _ringPos;
    private int[] _xMap = [];
    private int[] _xMapChroma = [];
    private int _mapSrcW, _mapDstW;
    private int _width, _height, _stride, _sliceHeight, _colorFormat;
    private bool _eos;
    private bool _disposed;

    public AndroidVideoDecoder(ReadOnlyMemory<byte> avcC, int width, int height, int maxSize)
    {
        _width = width;
        _height = height;
        _maxSize = Math.Max(16, maxSize);
        _bt709 = width >= 1280 || height >= 720;
        var (sps, pps, lengthSize) = ParseAvcC(avcC.Span);
        _nalLengthSize = lengthSize;

        var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, width, height);
        format.SetByteBuffer("csd-0", Java.Nio.ByteBuffer.Wrap(sps));
        format.SetByteBuffer("csd-1", Java.Nio.ByteBuffer.Wrap(pps));
        format.SetInteger(MediaFormat.KeyColorFormat, ColorFormatYuv420Flexible);
        _codec = MediaCodec.CreateDecoderByType(MediaFormat.MimetypeVideoAvc)!;
        try
        {
            _codec.Configure(format, null, null, MediaCodecConfigFlags.None);
            _codec.Start();
        }
        catch
        {
            _codec.Release();
            throw;
        }
    }

    private static (byte[] Sps, byte[] Pps, int LengthSize) ParseAvcC(ReadOnlySpan<byte> c)
    {
        if (c.Length < 7 || c[0] != 1)
        {
            throw new InvalidDataException("Invalid avcC.");
        }
        var lengthSize = (c[4] & 3) + 1;
        var sps = new List<byte>();
        var pps = new List<byte>();
        var p = 5;
        var count = c[p++] & 0x1F;
        for (var i = 0; i < count; i++)
        {
            var n = (c[p] << 8) | c[p + 1];
            p += 2;
            sps.AddRange([0, 0, 0, 1]);
            sps.AddRange(c.Slice(p, n).ToArray());
            p += n;
        }
        count = c[p++];
        for (var i = 0; i < count; i++)
        {
            var n = (c[p] << 8) | c[p + 1];
            p += 2;
            pps.AddRange([0, 0, 0, 1]);
            pps.AddRange(c.Slice(p, n).ToArray());
            p += n;
        }
        return (sps.ToArray(), pps.ToArray(), lengthSize);
    }

    public void Decode(ReadOnlySpan<byte> accessUnit, long timestamp, bool keyFrame, Action<DecodedVideoFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_eos)
        {
            Flush();
        }
        var size = ToAnnexB(accessUnit);
        if (size == 0)
        {
            return;
        }
        var stalls = 0;
        int index;
        while ((index = _codec.DequeueInputBuffer(5_000)) < 0)
        {
            DrainOutput(output, 0);
            if (++stalls > 400)
            {
                throw new InvalidOperationException("Video decoder stalled.");
            }
        }
        var buf = _codec.GetInputBuffer(index)!;
        buf.Clear();
        if (size > buf.Capacity())
        {
            _codec.QueueInputBuffer(index, 0, 0, 0, 0);
            throw new InvalidDataException("Video access unit larger than the decoder input buffer.");
        }
        buf.Put(_input, 0, size);
        _codec.QueueInputBuffer(index, 0, size, timestamp / 10, keyFrame ? MediaCodecBufferFlags.KeyFrame : 0);
        DrainOutput(output, 2_000);
    }

    public void Drain(Action<DecodedVideoFrame> output)
    {
        if (_disposed || _eos)
        {
            return;
        }
        var index = -1;
        for (var i = 0; i < 200 && index < 0; i++)
        {
            index = _codec.DequeueInputBuffer(10_000);
            if (index < 0)
            {
                DrainOutput(output, 0);
            }
        }
        if (index < 0)
        {
            return;
        }
        _codec.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
        _eos = true;
        for (var i = 0; i < 200; i++)
        {
            if (DrainOutput(output, 20_000))
            {
                break;
            }
        }
    }

    public void Flush()
    {
        if (_disposed)
        {
            return;
        }
        _codec.Flush();
        if (_eos)
        {
            _codec.Start(); // flushing after end-of-stream requires a restart
            _eos = false;
        }
    }

    /// <summary>Converts length-prefixed NAL units to start-code framing in <see cref="_input"/>; returns the byte count.</summary>
    private int ToAnnexB(ReadOnlySpan<byte> au)
    {
        var need = au.Length + au.Length / 2 + 16;
        if (_input.Length < need)
        {
            _input = new byte[need];
        }
        var o = 0;
        var p = 0;
        while (p + _nalLengthSize <= au.Length)
        {
            var n = 0;
            for (var i = 0; i < _nalLengthSize; i++)
            {
                n = (n << 8) | au[p + i];
            }
            p += _nalLengthSize;
            if (n <= 0 || p + n > au.Length)
            {
                break;
            }
            if (o + n + 4 > _input.Length)
            {
                Array.Resize(ref _input, (o + n + 4) * 2);
            }
            _input[o++] = 0;
            _input[o++] = 0;
            _input[o++] = 0;
            _input[o++] = 1;
            au.Slice(p, n).CopyTo(_input.AsSpan(o));
            o += n;
            p += n;
        }
        return o;
    }

    private bool DrainOutput(Action<DecodedVideoFrame> output, long firstTimeoutUs)
    {
        var timeout = firstTimeoutUs;
        while (true)
        {
            var index = _codec.DequeueOutputBuffer(_info, timeout);
            timeout = 0;
            if (index == (int)MediaCodecInfoState.TryAgainLater)
            {
                return false;
            }
            if (index == (int)MediaCodecInfoState.OutputFormatChanged)
            {
                ReadFormat();
                continue;
            }
            if (index < 0)
            {
                continue;
            }
            var flags = _info.Flags;
            try
            {
                if (_info.Size > 0)
                {
                    var frame = ConvertOutput(index);
                    if (frame is { } f)
                    {
                        output(new DecodedVideoFrame(f, _info.PresentationTimeUs * 10));
                    }
                }
            }
            finally
            {
                _codec.ReleaseOutputBuffer(index, false);
            }
            if (flags.HasFlag(MediaCodecBufferFlags.EndOfStream))
            {
                return true;
            }
        }
    }

    private void ReadFormat()
    {
        var f = _codec.OutputFormat;
        if (f is null)
        {
            return;
        }
        _width = f.ContainsKey(MediaFormat.KeyWidth) ? f.GetInteger(MediaFormat.KeyWidth) : _width;
        _height = f.ContainsKey(MediaFormat.KeyHeight) ? f.GetInteger(MediaFormat.KeyHeight) : _height;
        _stride = f.ContainsKey("stride") ? f.GetInteger("stride") : _width;
        _sliceHeight = f.ContainsKey("slice-height") ? f.GetInteger("slice-height") : _height;
        _colorFormat = f.ContainsKey(MediaFormat.KeyColorFormat) ? f.GetInteger(MediaFormat.KeyColorFormat) : ColorFormatYuv420Flexible;
    }

    private PreviewFrame? ConvertOutput(int index)
    {
        var image = _codec.GetOutputImage(index);
        if (image is not null)
        {
            using (image)
            {
                var crop = image.CropRect;
                var planes = image.GetPlanes()!;
                for (var i = 0; i < 3; i++)
                {
                    var b = planes[i].Buffer!;
                    b.Rewind();
                    var n = b.Remaining();
                    if (_plane[i].Length < n)
                    {
                        _plane[i] = new byte[n];
                    }
                    b.Get(_plane[i], 0, n);
                }
                return Convert(
                    _plane[0], 0, planes[0].RowStride,
                    _plane[1], 0, planes[1].RowStride, planes[1].PixelStride,
                    _plane[2], 0, planes[2].RowStride, planes[2].PixelStride,
                    crop!.Left, crop.Top, crop.Width(), crop.Height());
            }
        }

        // Fallback: raw ByteBuffer in planar / semi-planar 4:2:0 using the output format's stride and slice height.
        var buf = _codec.GetOutputBuffer(index);
        if (buf is null || (_colorFormat != ColorFormatYuv420Planar && _colorFormat != ColorFormatYuv420SemiPlanar))
        {
            return null;
        }
        buf.Position(_info.Offset);
        buf.Limit(_info.Offset + _info.Size);
        if (_plane[0].Length < _info.Size)
        {
            _plane[0] = new byte[_info.Size];
        }
        buf.Get(_plane[0], 0, _info.Size);
        var stride = Math.Max(_stride, _width);
        var slice = Math.Max(_sliceHeight, _height);
        var yEnd = stride * slice;
        if (_colorFormat == ColorFormatYuv420SemiPlanar)
        {
            return Convert(_plane[0], 0, stride, _plane[0], yEnd, stride, 2, _plane[0], yEnd + 1, stride, 2, 0, 0, _width, _height);
        }
        var cStride = stride / 2;
        var uEnd = yEnd + cStride * (slice / 2);
        return Convert(_plane[0], 0, stride, _plane[0], yEnd, cStride, 1, _plane[0], uEnd, cStride, 1, 0, 0, _width, _height);
    }

    private PreviewFrame Convert(
        byte[] yb, int yOff, int yRow,
        byte[] ub, int uOff, int uRow, int uPix,
        byte[] vb, int vOff, int vRow, int vPix,
        int left, int top, int w, int h)
    {
        var scale = Math.Min(1.0, (double)_maxSize / Math.Max(w, h));
        var dw = Math.Max(2, (int)(w * scale) & ~1);
        var dh = Math.Max(2, (int)(h * scale) & ~1);
        if (dw > w || dh > h)
        {
            dw = w & ~1;
            dh = h & ~1;
        }
        if (_mapSrcW != w || _mapDstW != dw)
        {
            _mapSrcW = w;
            _mapDstW = dw;
            _xMap = new int[dw];
            _xMapChroma = new int[dw];
            for (var x = 0; x < dw; x++)
            {
                var sx = (int)((long)x * w / dw);
                _xMap[x] = left + sx;
                _xMapChroma[x] = (left + sx) >> 1;
            }
        }

        var stride = dw * 4;
        var ring = _ring[_ringPos];
        if (ring is null || ring.Length != stride * dh)
        {
            ring = _ring[_ringPos] = new byte[stride * dh];
        }
        _ringPos = (_ringPos + 1) % RingSize;

        int rv, gu, gv, bu;
        if (_bt709) { rv = 459; gu = 55; gv = 136; bu = 541; }
        else { rv = 409; gu = 100; gv = 208; bu = 516; }

        var xMap = _xMap;
        var xMapC = _xMapChroma;
        for (var y = 0; y < dh; y++)
        {
            var sy = top + (int)((long)y * h / dh);
            var yRowOff = yOff + sy * yRow;
            var uRowOff = uOff + (sy >> 1) * uRow;
            var vRowOff = vOff + (sy >> 1) * vRow;
            var o = y * stride;
            for (var x = 0; x < dw; x++)
            {
                var c = 298 * (yb[yRowOff + xMap[x]] - 16);
                var d = ub[uRowOff + xMapC[x] * uPix] - 128;
                var e = vb[vRowOff + xMapC[x] * vPix] - 128;
                var r = (c + rv * e + 128) >> 8;
                var g = (c - gu * d - gv * e + 128) >> 8;
                var b = (c + bu * d + 128) >> 8;
                ring[o] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                ring[o + 1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                ring[o + 2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                ring[o + 3] = 255;
                o += 4;
            }
        }
        return new PreviewFrame(ring, dw, dh, stride);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try { _codec.Stop(); } catch (Java.Lang.IllegalStateException) { }
        _codec.Release();
        _codec.Dispose();
    }
}
