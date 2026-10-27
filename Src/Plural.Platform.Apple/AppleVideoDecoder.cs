using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Platform;

namespace Plural.Platform.Apple;

/// <summary>H.264 playback decoding on VideoToolbox (macOS and iOS).</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed class AppleVideoDecoderFactory : IVideoDecoderFactory
{
    public bool CanDecode(string codec) => string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase);

    public IVideoDecoder Create(string codec, ReadOnlyMemory<byte> codecPrivate, int width, int height, int maxSize)
    {
        if (!CanDecode(codec))
        {
            throw new NotSupportedException($"Codec '{codec}' is not supported.");
        }
        return new AppleVideoDecoder(codecPrivate, width, height, maxSize);
    }
}

/// <summary>
/// Synchronous VTDecompressionSession. VideoToolbox scales to the output size and converts to BGRA; frames come out in decode order, so a
/// small reorder buffer (grown when a stream turns out to have B-frames) returns them in presentation order. A frame's pixels stay valid
/// for at least the next <see cref="RingSize"/> − <see cref="MaxReorder"/> frames.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed unsafe class AppleVideoDecoder : IVideoDecoder
{
    private const int RingSize = 16;
    private const int MaxReorder = 4;

    private readonly byte[][] _ring = new byte[RingSize][];
    private readonly List<DecodedVideoFrame> _decoded = [];   // filled by the output callback
    private readonly List<DecodedVideoFrame> _held = [];      // waiting for presentation order
    private readonly int _outW, _outH;
    private GCHandle _self;
    private IntPtr _format;
    private IntPtr _session;
    private int _ringPos;
    private int _reorder;
    private long _lastOut = long.MinValue;
    private bool _disposed;

    public AppleVideoDecoder(ReadOnlyMemory<byte> avcC, int width, int height, int maxSize)
    {
        maxSize = Math.Max(16, maxSize);
        var scale = Math.Min(1.0, (double)maxSize / Math.Max(1, Math.Max(width, height)));
        _outW = Math.Max(2, (int)Math.Round(width * scale) & ~1);
        _outH = Math.Max(2, (int)Math.Round(height * scale) & ~1);
        _self = GCHandle.Alloc(this);
        try
        {
            _format = CreateFormat(avcC.Span);
            CreateSession();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static IntPtr CreateFormat(ReadOnlySpan<byte> c)
    {
        if (c.Length < 7 || c[0] != 1)
        {
            throw new InvalidDataException("Invalid avcC.");
        }
        var lengthSize = (c[4] & 3) + 1;
        var sets = new List<byte[]>();
        var p = 5;
        var spsCount = c[p++] & 0x1F;
        for (var i = 0; i < spsCount; i++)
        {
            var n = (c[p] << 8) | c[p + 1];
            sets.Add(c.Slice(p + 2, n).ToArray());
            p += 2 + n;
        }
        var ppsCount = c[p++];
        for (var i = 0; i < ppsCount; i++)
        {
            var n = (c[p] << 8) | c[p + 1];
            sets.Add(c.Slice(p + 2, n).ToArray());
            p += 2 + n;
        }
        var handles = sets.Select(s => GCHandle.Alloc(s, GCHandleType.Pinned)).ToArray();
        try
        {
            var pointers = stackalloc byte*[handles.Length];
            var sizes = stackalloc nuint[handles.Length];
            for (var i = 0; i < handles.Length; i++)
            {
                pointers[i] = (byte*)handles[i].AddrOfPinnedObject();
                sizes[i] = (nuint)sets[i].Length;
            }
            IntPtr format;
            VT.Check(CM.CMVideoFormatDescriptionCreateFromH264ParameterSets(IntPtr.Zero, (nuint)handles.Length, pointers, sizes, lengthSize, &format),
                "Reading the H.264 parameter sets");
            return format;
        }
        finally
        {
            foreach (var h in handles)
            {
                h.Free();
            }
        }
    }

    private void CreateSession()
    {
        var attributes = CV.BgraAttributes(_outW, _outH);
        try
        {
            var callback = new VTDecompressionOutputCallbackRecord
            {
                Callback = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, uint, IntPtr, void>)&OnDecoded,
                RefCon = GCHandle.ToIntPtr(_self),
            };
            IntPtr session;
            VT.Check(VT.VTDecompressionSessionCreate(IntPtr.Zero, _format, IntPtr.Zero, attributes, &callback, &session), "Creating the H.264 decoder");
            _session = session;
        }
        finally
        {
            CF.CFRelease(attributes);
        }
    }

    private void DestroySession()
    {
        if (_session != IntPtr.Zero)
        {
            VT.VTDecompressionSessionWaitForAsynchronousFrames(_session);
            VT.VTDecompressionSessionInvalidate(_session);
            CF.CFRelease(_session);
            _session = IntPtr.Zero;
        }
    }

    public void Decode(ReadOnlySpan<byte> accessUnit, long timestamp, bool keyFrame, Action<DecodedVideoFrame> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (accessUnit.IsEmpty)
        {
            return;
        }
        IntPtr block = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            var length = (nuint)accessUnit.Length;
            VT.Check(CM.CMBlockBufferCreateWithMemoryBlock(IntPtr.Zero, null, length, IntPtr.Zero, null, 0, length, 0, &block), "CMBlockBuffer");
            fixed (byte* p = accessUnit)
            {
                VT.Check(CM.CMBlockBufferReplaceDataBytes(p, block, 0, length), "CMBlockBuffer");
            }
            var timing = new CMSampleTimingInfo
            {
                Duration = CMTime.Invalid,
                PresentationTimeStamp = CMTime.FromTicks(timestamp),
                DecodeTimeStamp = CMTime.Invalid,
            };
            var size = length;
            VT.Check(CM.CMSampleBufferCreateReady(IntPtr.Zero, block, _format, 1, 1, &timing, 1, &size, &sample), "CMSampleBuffer");

            uint info;
            var status = VT.VTDecompressionSessionDecodeFrame(_session, sample, 0, (IntPtr)timestamp, &info);
            if (status == VT.InvalidSessionErr)
            {
                // iOS invalidates sessions while the app is in the background; start over (frames resume at the next key frame).
                DestroySession();
                CreateSession();
                status = keyFrame ? VT.VTDecompressionSessionDecodeFrame(_session, sample, 0, (IntPtr)timestamp, &info) : 0;
            }
            if (status != 0)
            {
                System.Diagnostics.Debug.WriteLine($"VTDecompressionSessionDecodeFrame: {status}");
            }
        }
        finally
        {
            if (sample != IntPtr.Zero)
            {
                CF.CFRelease(sample);
            }
            if (block != IntPtr.Zero)
            {
                CF.CFRelease(block);
            }
        }
        Deliver(output, _reorder);
    }

    public void Drain(Action<DecodedVideoFrame> output)
    {
        if (_disposed || _session == IntPtr.Zero)
        {
            return;
        }
        VT.VTDecompressionSessionFinishDelayedFrames(_session);
        VT.VTDecompressionSessionWaitForAsynchronousFrames(_session);
        Deliver(output, 0);
    }

    public void Flush()
    {
        if (_session != IntPtr.Zero)
        {
            VT.VTDecompressionSessionWaitForAsynchronousFrames(_session);
        }
        lock (_decoded)
        {
            _decoded.Clear();
        }
        _held.Clear();
        _lastOut = long.MinValue;
    }

    /// <summary>Moves decoded frames into the reorder buffer and outputs all but the newest <paramref name="keep"/> in timestamp order.</summary>
    private void Deliver(Action<DecodedVideoFrame> output, int keep)
    {
        lock (_decoded)
        {
            foreach (var f in _decoded)
            {
                if (f.Timestamp <= _lastOut)
                {
                    // Came out after a later frame was shown: the stream reorders (B-frames). Hold more frames from now on.
                    _reorder = Math.Min(MaxReorder, _reorder + 1);
                    keep = Math.Max(keep, _reorder);
                    continue;
                }
                var at = _held.FindIndex(h => h.Timestamp > f.Timestamp);
                _held.Insert(at < 0 ? _held.Count : at, f);
            }
            _decoded.Clear();
        }
        while (_held.Count > keep)
        {
            var f = _held[0];
            _held.RemoveAt(0);
            _lastOut = f.Timestamp;
            output(f);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnDecoded(IntPtr refCon, IntPtr frameRefCon, int status, uint flags, IntPtr image)
    {
        try
        {
            if (status != 0 || image == IntPtr.Zero || GCHandle.FromIntPtr(refCon).Target is not AppleVideoDecoder self)
            {
                return;
            }
            self.Copy(image, (long)frameRefCon);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("H.264 decode output failed: " + ex.Message);
        }
    }

    private void Copy(IntPtr image, long timestamp)
    {
        var w = (int)CV.CVPixelBufferGetWidth(image);
        var h = (int)CV.CVPixelBufferGetHeight(image);
        if (w <= 0 || h <= 0 || CV.CVPixelBufferGetPixelFormatType(image) != CV.PixelFormatBgra
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
            byte[] dst;
            lock (_decoded)
            {
                dst = _ring[_ringPos];
                if (dst is null || dst.Length != w * h * 4)
                {
                    dst = _ring[_ringPos] = new byte[w * h * 4];
                }
                _ringPos = (_ringPos + 1) % RingSize;
            }
            CV.ScaleBgra(src, (int)CV.CVPixelBufferGetBytesPerRow(image), w, h, dst, w, h);
            lock (_decoded)
            {
                _decoded.Add(new DecodedVideoFrame(new PreviewFrame(dst, w, h, w * 4), timestamp));
            }
        }
        finally
        {
            CV.CVPixelBufferUnlockBaseAddress(image, CV.LockReadOnly);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        DestroySession();
        if (_format != IntPtr.Zero)
        {
            CF.CFRelease(_format);
            _format = IntPtr.Zero;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
