using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Plural.Core.Platform;
using Vortice.MediaFoundation;

namespace Plural.Platform.Windows;

/// <summary>Creates H.264 decoders on the Microsoft Media Foundation software decoder MFT (NV12 out, converted/scaled to BGRA).</summary>
[SupportedOSPlatform("windows")]
public sealed class MfVideoDecoderFactory : IVideoDecoderFactory
{
    private bool? _can;

    public bool CanDecode(string codec)
    {
        if (!codec.Equals("h264", StringComparison.OrdinalIgnoreCase)) return false;
        if (_can is { } known) return known;
        try
        {
            using var d = MfVideoDecoder.Create(640, 360, 640, default);
            _can = true;
        }
        catch { _can = false; }
        return _can.Value;
    }

    public IVideoDecoder Create(string codec, ReadOnlyMemory<byte> codecPrivate, int width, int height, int maxSize)
    {
        if (!codec.Equals("h264", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException($"Codec '{codec}' is not supported.");
        try { return MfVideoDecoder.Create(width, height, maxSize, codecPrivate); }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            throw new NotSupportedException($"Media Foundation could not create an H.264 decoder: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// H.264 decoder MFT wrapper. Software decoder (no D3D manager), synchronous, used from a single thread.
/// AVCC access units are converted to Annex-B (SPS/PPS from avcC prepended to key frames).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MfVideoDecoder : IVideoDecoder
{
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private readonly IMFTransform _mft;
    private readonly int _maxSize;
    private readonly byte[] _paramSets;      // Annex-B SPS+PPS
    private readonly int _nalLengthSize;
    private byte[] _annexB = new byte[1 << 16];
    private bool _provides;
    private int _outSize;
    private bool _disposed;

    // Current output geometry (NV12 in the MFT buffer).
    private int _srcW, _srcH, _stride;
    private int _dstW, _dstH;
    private int[] _xMap = [];
    private bool _bt709;

    private MfVideoDecoder(IMFTransform mft, int maxSize, ReadOnlySpan<byte> avcC)
    {
        _mft = mft;
        _maxSize = Math.Max(16, maxSize);
        _nalLengthSize = 4;
        _paramSets = [];
        if (avcC.Length >= 7)
        {
            _nalLengthSize = (avcC[4] & 3) + 1;
            _paramSets = ParseAvcC(avcC);
        }
    }

    private static byte[] ParseAvcC(ReadOnlySpan<byte> c)
    {
        var o = new List<byte>();
        int p = 5;
        for (int pass = 0; pass < 2 && p < c.Length; pass++)
        {
            int count = pass == 0 ? c[p++] & 0x1F : c[p++];
            for (int i = 0; i < count && p + 2 <= c.Length; i++)
            {
                int len = (c[p] << 8) | c[p + 1];
                p += 2;
                if (p + len > c.Length) break;
                o.AddRange([0, 0, 0, 1]);
                o.AddRange(c.Slice(p, len).ToArray());
                p += len;
            }
        }
        return o.ToArray();
    }

    public static MfVideoDecoder Create(int width, int height, int maxSize, ReadOnlyMemory<byte> avcC)
    {
        MfCommon.EnsureStarted();
        var activates = MfCommon.EnumTransforms(TransformCategoryGuids.VideoDecoder, MfCommon.EnumSync | MfCommon.EnumSortAndFilter,
            VideoFormatGuids.H264, null, MediaTypeGuids.Video);
        try
        {
            if (activates.Count == 0) throw new PlatformNotSupportedException("No H.264 decoder found in Media Foundation.");
            Exception? last = null;
            foreach (var act in activates)
            {
                IMFTransform? mft = null;
                try
                {
                    mft = MfCommon.ActivateTransform(act);
                    var dec = new MfVideoDecoder(mft, maxSize, avcC.Span);
                    dec.Configure(width, height);
                    return dec;
                }
                catch (Exception ex) { last = ex; mft?.Dispose(); }
            }
            throw new PlatformNotSupportedException("No usable H.264 decoder.", last);
        }
        finally { foreach (var a in activates) a.Dispose(); }
    }

    private static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

    private void Configure(int width, int height)
    {
        try
        {
            using var attrs = _mft.Attributes;
            attrs.Set(MfLowLatency, 1u);
        }
        catch { /* optional */ }

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            inType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            _mft.SetInputType(0, inType, 0);
        }
        SelectNv12Output();
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private void SelectNv12Output()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _mft.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { throw new PlatformNotSupportedException("H.264 decoder offers no NV12 output."); }
            using (t)
            {
                if (t.GetGUID(MediaTypeAttributeKeys.Subtype) != VideoFormatGuids.NV12) continue;
                _mft.SetOutputType(0, t, 0);
            }
            break;
        }
        ReadOutputGeometry();
        var info = _mft.GetOutputStreamInfo(0);
        _provides = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        _outSize = Math.Max(info.Size, _srcW * _srcH * 3 / 2);
    }

    private void ReadOutputGeometry()
    {
        using var cur = _mft.GetOutputCurrentType(0);
        ulong fs = cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        int w = (int)(fs >> 32), h = (int)(fs & 0xFFFFFFFF);
        // Crop to the display aperture when the MFT reports a padded frame (e.g. 1088 for 1080 content).
        try
        {
            var area = cur.GetBlob(MediaTypeAttributeKeys.MinimumDisplayAperture);   // MFVideoArea: offsetX(4) offsetY(4) cx(4) cy(4)
            if (area is { Length: >= 16 })
            {
                int cx = BitConverter.ToInt32(area, 8), cy = BitConverter.ToInt32(area, 12);
                if (cx > 0 && cy > 0 && cx <= w && cy <= h) { w = cx; h = cy; }
            }
        }
        catch { }
        int stride = w;
        try { stride = Math.Abs((int)cur.GetUInt32(MediaTypeAttributeKeys.DefaultStride)); } catch { }
        if (stride < w)
        {
            // frame size may be the padded size; stride at least the coded width
            stride = (int)(fs >> 32);
        }
        _srcW = w; _srcH = h; _stride = Math.Max(stride, w);
        _bt709 = h >= 720;

        double scale = Math.Min(1.0, (double)_maxSize / Math.Max(w, h));
        _dstW = Math.Max(2, (int)Math.Round(w * scale));
        _dstH = Math.Max(2, (int)Math.Round(h * scale));
        _xMap = new int[_dstW];
        for (int x = 0; x < _dstW; x++) _xMap[x] = Math.Min(_srcW - 1, (int)((x + 0.5) * _srcW / _dstW));
    }

    public void Decode(ReadOnlySpan<byte> accessUnit, long timestamp, bool keyFrame, Action<DecodedVideoFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (accessUnit.IsEmpty) return;
        Pull(output);

        int needed = accessUnit.Length * 2 + _paramSets.Length + 64;
        if (_annexB.Length < needed) _annexB = new byte[needed + needed / 2];
        int n = 0;
        if (keyFrame && _paramSets.Length > 0)
        {
            _paramSets.CopyTo(_annexB, 0);
            n = _paramSets.Length;
        }
        int ls = _nalLengthSize;
        int p = 0;
        while (p + ls <= accessUnit.Length)
        {
            int len = 0;
            for (int i = 0; i < ls; i++) len = (len << 8) | accessUnit[p + i];
            p += ls;
            if (len <= 0 || p + len > accessUnit.Length) break;
            _annexB[n++] = 0; _annexB[n++] = 0; _annexB[n++] = 0; _annexB[n++] = 1;
            accessUnit.Slice(p, len).CopyTo(_annexB.AsSpan(n));
            n += len;
            p += len;
        }
        if (n == 0) return;
        // Four-byte start codes can take up to 4 - ls extra bytes per NAL; the buffer above has slack, grow if needed.
        using var sample = MfCommon.CreateSample(n);
        using (var buf = sample.GetBufferByIndex(0))
        {
            buf.Lock(out IntPtr ptr, out _, out _);
            try { new ReadOnlySpan<byte>(_annexB, 0, n).CopyTo(new Span<byte>((void*)ptr, n)); }
            finally { buf.Unlock(); }
            buf.CurrentLength = n;
        }
        sample.SampleTime = timestamp;
        sample.SampleDuration = 333_333;
        if (keyFrame) sample.Set(new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a"), 1u);
        _mft.ProcessInput(0, sample, 0);
        Pull(output);
    }

    public void Drain(Action<DecodedVideoFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        Pull(output);
        _mft.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mft.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private void Pull(Action<DecodedVideoFrame> output)
    {
        while (true)
        {
            IMFSample? own = _provides ? null : MfCommon.CreateSample(_outSize);
            var buffer = new OutputDataBuffer { StreamID = 0, Sample = own };
            var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            var produced = buffer.Sample;
            try
            {
                if (hr.Code == MfCommon.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                if (hr.Code == MfCommon.MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    SelectNv12Output();
                    if (own is not null) { own.Dispose(); own = null; }
                    continue;
                }
                hr.CheckError();
                if (produced is null) return;
                Convert(produced, output);
            }
            finally
            {
                if (own is not null) own.Dispose();
                else produced?.Dispose();
            }
        }
    }

    private void Convert(IMFSample sample, Action<DecodedVideoFrame> output)
    {
        long ts = sample.SampleTime;
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr ptr, out _, out int len);
        try
        {
            int stride = _stride;
            if (stride <= 0 || _srcW <= 0 || _srcH <= 0) return; // no valid output type yet (would divide by zero)
            // Total luma rows in the buffer (padded height); the chroma plane follows the luma plane.
            int lumaRows = (int)((long)len * 2 / (3L * stride));
            if (lumaRows < _srcH) return;
            byte* y0 = (byte*)ptr;
            byte* uv0 = y0 + (long)stride * lumaRows;

            int dw = _dstW, dh = _dstH;
            int outStride = dw * 4;
            var bgra = new byte[outStride * dh];
            int[] xmap = _xMap;
            bool hd = _bt709;
            fixed (byte* dst0 = bgra)
            fixed (int* xm = xmap)
            {
                for (int y = 0; y < dh; y++)
                {
                    int sy = Math.Min(_srcH - 1, (int)((y + 0.5) * _srcH / dh));
                    byte* yr = y0 + (long)sy * stride;
                    byte* uvr = uv0 + (long)(sy >> 1) * stride;
                    byte* d = dst0 + (long)y * outStride;
                    for (int x = 0; x < dw; x++)
                    {
                        int sx = xm[x];
                        int Y = yr[sx] - 16;
                        int U = uvr[sx & ~1] - 128;
                        int V = uvr[(sx & ~1) + 1] - 128;
                        int c = 298 * Y + 128;
                        int r, g, b;
                        if (hd)
                        {
                            r = (c + 459 * V) >> 8;
                            g = (c - 55 * U - 136 * V) >> 8;
                            b = (c + 541 * U) >> 8;
                        }
                        else
                        {
                            r = (c + 409 * V) >> 8;
                            g = (c - 100 * U - 208 * V) >> 8;
                            b = (c + 516 * U) >> 8;
                        }
                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;
                        d += 4;
                    }
                }
            }
            output(new DecodedVideoFrame(new PreviewFrame(bgra, dw, dh, outStride), ts));
        }
        finally { buffer.Unlock(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        _mft.Dispose();
    }
}
