using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Platform;

namespace Plural.Platform.Apple;

/// <summary>
/// Real-time H.264 encoder on a VTCompressionSession (hardware on Apple silicon/iPhone). Takes capture pixel buffers and emits Annex-B
/// access units; key frames carry SPS/PPS in front. No frame reordering (B-frames), so output order is input order and one timestamp
/// per sample is enough. The frame's MediaClock timestamp travels as the source-frame refcon.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed unsafe class VtH264Encoder : IDisposable
{
    private static readonly byte[] StartCode = [0, 0, 0, 1];

    private readonly Action<EncodedSample> _output;
    private GCHandle _self;
    private IntPtr _session;
    private IntPtr _forceKeyFrame;
    private volatile bool _active = true;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Frames handed to <see cref="Encode"/> so far.</summary>
    public int Frames { get; private set; }

    public VtH264Encoder(int width, int height, int frameRate, int bitRate, Action<EncodedSample> output)
    {
        Width = width;
        Height = height;
        _output = output;
        _self = GCHandle.Alloc(this);
        var spec = IntPtr.Zero;
        try
        {
            // macOS: prefer the hardware encoder (falls back to software when there is none). The key does not exist on older iOS.
            var hwKey = VT.Key("kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder");
            if (hwKey != IntPtr.Zero && OperatingSystem.IsMacOS())
            {
                spec = CFx.CreateDictionary();
                CFx.Set(spec, hwKey, CFx.True);
            }
            IntPtr session;
            VT.Check(VT.VTCompressionSessionCreate(IntPtr.Zero, width, height, VT.CodecH264, spec, IntPtr.Zero, IntPtr.Zero,
                &OnEncoded, GCHandle.ToIntPtr(_self), &session), "Creating the H.264 encoder");
            _session = session;

            SetProperty("kVTCompressionPropertyKey_RealTime", CFx.True);
            SetProperty("kVTCompressionPropertyKey_AllowFrameReordering", CFx.False);
            SetProperty("kVTCompressionPropertyKey_ProfileLevel", VT.Key("kVTProfileLevel_H264_High_AutoLevel"));
            SetNumber("kVTCompressionPropertyKey_AverageBitRate", CFx.Number(bitRate));
            SetNumber("kVTCompressionPropertyKey_ExpectedFrameRate", CFx.Number(frameRate));
            // ~1 s GOP, like the other platforms (a resumed take waits at most that long for a key frame if forcing one fails).
            SetNumber("kVTCompressionPropertyKey_MaxKeyFrameInterval", CFx.Number(Math.Max(1, frameRate)));
            SetNumber("kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration", CFx.Number(1.0));
            VT.Check(VT.VTCompressionSessionPrepareToEncodeFrames(session), "Preparing the H.264 encoder");

            _forceKeyFrame = CFx.CreateDictionary();
            CFx.Set(_forceKeyFrame, VT.Key("kVTEncodeFrameOptionKey_ForceKeyFrame"), CFx.True);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (spec != IntPtr.Zero)
            {
                CF.CFRelease(spec);
            }
        }
    }

    /// <summary>Encodes one capture frame (a CVPixelBuffer of <see cref="Width"/>×<see cref="Height"/>) stamped <paramref name="ticks"/>.</summary>
    public void Encode(IntPtr pixelBuffer, long ticks, bool keyFrame)
    {
        if (_session == IntPtr.Zero)
        {
            return;
        }
        Frames++;
        uint info;
        var status = VT.VTCompressionSessionEncodeFrame(_session, pixelBuffer, CMTime.FromTicks(ticks), CMTime.Invalid,
            keyFrame ? _forceKeyFrame : IntPtr.Zero, (IntPtr)ticks, &info);
        if (status != 0)
        {
            System.Diagnostics.Debug.WriteLine($"VTCompressionSessionEncodeFrame: {status}");
        }
    }

    private void SetProperty(string key, IntPtr value)
    {
        var k = VT.Key(key);
        if (k != IntPtr.Zero && value != IntPtr.Zero)
        {
            // Unsupported optional properties are not fatal.
            _ = VT.VTSessionSetProperty(_session, k, value);
        }
    }

    private void SetNumber(string key, IntPtr number)
    {
        try
        {
            SetProperty(key, number);
        }
        finally
        {
            CF.CFRelease(number);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEncoded(IntPtr refCon, IntPtr frameRefCon, int status, uint flags, IntPtr sampleBuffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(refCon).Target is not VtH264Encoder self || !self._active || status != 0 || sampleBuffer == IntPtr.Zero
                || (flags & VT.EncodeInfoFrameDropped) != 0)
            {
                return;
            }
            var key = CM.IsSyncSample(sampleBuffer);
            var data = ToAnnexB(sampleBuffer, key);
            if (data.Length > 0)
            {
                self._output(new EncodedSample(data, (long)frameRefCon, key));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("H.264 output failed: " + ex.Message);
        }
    }

    /// <summary>AVCC (length-prefixed NAL units) → Annex-B, with the parameter sets in front of key frames.</summary>
    private static byte[] ToAnnexB(IntPtr sampleBuffer, bool key)
    {
        var block = CM.CMSampleBufferGetDataBuffer(sampleBuffer);
        if (block == IntPtr.Zero)
        {
            return [];
        }
        var length = (int)CM.CMBlockBufferGetDataLength(block);
        var avcc = new byte[length];
        fixed (byte* p = avcc)
        {
            if (CM.CMBlockBufferCopyDataBytes(block, 0, (nuint)length, p) != 0)
            {
                return [];
            }
        }

        var format = CM.CMSampleBufferGetFormatDescription(sampleBuffer);
        var lengthSize = 4;
        using var ms = new MemoryStream(length + 64);
        if (format != IntPtr.Zero)
        {
            nuint count = 0;
            int header = 0;
            if (CM.CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, 0, null, null, &count, &header) == 0)
            {
                if (header is >= 1 and <= 4)
                {
                    lengthSize = header;
                }
                for (nuint i = 0; key && i < count; i++)
                {
                    byte* ps;
                    nuint size;
                    if (CM.CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, i, &ps, &size, null, null) == 0)
                    {
                        ms.Write(StartCode);
                        ms.Write(new ReadOnlySpan<byte>(ps, (int)size));
                    }
                }
            }
        }

        var pos = 0;
        while (pos + lengthSize <= length)
        {
            var n = 0;
            for (var i = 0; i < lengthSize; i++)
            {
                n = (n << 8) | avcc[pos + i];
            }
            pos += lengthSize;
            if (n <= 0 || pos + n > length)
            {
                break;
            }
            ms.Write(StartCode);
            ms.Write(avcc, pos, n);
            pos += n;
        }
        return ms.ToArray();
    }

    /// <summary>Stops output, waits for frames in flight and releases the session.</summary>
    public void Dispose()
    {
        _active = false;
        var session = _session;
        _session = IntPtr.Zero;
        if (session != IntPtr.Zero)
        {
            VT.VTCompressionSessionCompleteFrames(session, CMTime.Invalid);
            VT.VTCompressionSessionInvalidate(session);
            CF.CFRelease(session);
        }
        if (_forceKeyFrame != IntPtr.Zero)
        {
            CF.CFRelease(_forceKeyFrame);
            _forceKeyFrame = IntPtr.Zero;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
