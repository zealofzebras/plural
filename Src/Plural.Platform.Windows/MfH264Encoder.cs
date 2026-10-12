using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Plural.Core.Platform;
using Vortice.MediaFoundation;

namespace Plural.Platform.Windows;

/// <summary>
/// H.264 encoder MFT wrapper (NV12 in, Annex-B out). Hardware MFTs are tried first, then software ones; both synchronous and
/// asynchronous (event driven) MFTs are supported. Used from a single thread (the capture thread).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MfH264Encoder : IDisposable
{
    private const int EventFlagNoWait = 1;

    private readonly IMFTransform _mft;
    private readonly IMFMediaEventGenerator? _events;   // non-null for async MFTs
    private readonly bool _provides;
    private readonly int _outSize;
    private readonly int _fps;
    private byte[]? _sequenceHeader;                     // Annex-B SPS+PPS
    private int _inputCredits;
    private IMFSample? _pending;                         // async: newest frame waiting for a NeedInput event
    private bool _awaitingKey = true;
    private long _keyRequestedAt;                        // MediaClock ticks of the pending request (0 = none); set from any thread, consumed on the encoding thread
    private bool _headerWanted;                          // next IDR must carry SPS/PPS
    private static readonly Guid PictureTypeKey = new("973704e6-cd14-483c-8f20-c9fc0928bad5");   // MFSampleExtension_VideoEncodePictureType
    private const uint PictureTypeIdr = 2;               // eAVEncH264PictureType_IDR
    private bool _drainComplete;
    private bool _disposed;

    public string Name { get; }
    public bool IsHardware { get; }
    public bool IsAsync => _events is not null;
    public int DroppedFrames { get; private set; }

    private MfH264Encoder(IMFTransform mft, string name, bool hardware, bool async, int w, int h, int fps, int bitRate)
    {
        _mft = mft;
        Name = name;
        IsHardware = hardware;
        _fps = fps;

        if (async)
        {
            using (var attrs = mft.Attributes) attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            _events = mft.QueryInterface<IMFMediaEventGenerator>();
        }

        // Output type (try High, then Main profile, then Baseline).
        Exception? last = null;
        bool ok = false;
        foreach (uint profile in new uint[] { 100, 77, 66 })
        {
            try
            {
                using var outType = MediaFactory.MFCreateMediaType();
                outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitRate);
                outType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)w, (uint)h));
                outType.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
                outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
                outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);   // progressive
                outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, profile);
                mft.SetOutputType(0, outType, 0);
                ok = true;
                break;
            }
            catch (Exception ex) { last = ex; }
        }
        if (!ok) throw new InvalidOperationException("H.264 encoder rejected the output type.", last);

        // Best-effort codec properties (ICodecAPI).
        MfCommon.SetCodecValue(mft, MfCommon.AvEncCommonRateControlMode, 0);               // CBR
        MfCommon.SetCodecValue(mft, MfCommon.AvEncCommonMeanBitRate, (uint)bitRate);
        MfCommon.SetCodecValue(mft, MfCommon.AvEncMpvGopSize, (uint)fps);                  // ~1 s GOP
        MfCommon.SetCodecValue(mft, MfCommon.AvEncMpvDefaultBPictureCount, 0);
        MfCommon.SetCodecValue(mft, MfCommon.AvEncCommonLowLatency, 1, isBool: true);

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            inType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)w, (uint)h));
            inType.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            mft.SetInputType(0, inType, 0);
        }

        try
        {
            using var cur = mft.GetOutputCurrentType(0);
            var hdr = cur.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            if (hdr is { Length: > 4 }) _sequenceHeader = hdr;
        }
        catch { /* sequence header may only be known after the first output */ }

        var info = mft.GetOutputStreamInfo(0);
        _provides = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        _outSize = Math.Max(info.Size, w * h);

        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        MfCommon.SetCodecValue(mft, MfCommon.AvEncVideoForceKeyFrame, 1);
    }

    private static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

    /// <summary>Creates the first working H.264 encoder (hardware first, then software). Throws if none works.</summary>
    public static MfH264Encoder Create(int w, int h, int fps, int bitRate, bool allowHardware, List<string>? log = null)
    {
        MfCommon.EnsureStarted();
        var attempts = new List<(uint flags, bool hw)>();
        if (allowHardware) attempts.Add((MfCommon.EnumHardware | MfCommon.EnumSortAndFilter, true));
        attempts.Add((MfCommon.EnumSync | MfCommon.EnumAsync | MfCommon.EnumSortAndFilter, false));

        Exception? lastError = null;
        foreach (var (flags, hw) in attempts)
        {
            List<IMFActivate> activates;
            try { activates = MfCommon.EnumTransforms(TransformCategoryGuids.VideoEncoder, flags, null, VideoFormatGuids.H264, MediaTypeGuids.Video); }
            catch (Exception ex) { lastError = ex; continue; }

            try
            {
                foreach (var act in activates)
                {
                    string name = "H.264 encoder";
                    try { name = act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { }
                    IMFTransform? mft = null;
                    try
                    {
                        mft = MfCommon.ActivateTransform(act);
                        bool async = false;
                        using (var attrs = mft.Attributes)
                        {
                            try { async = attrs.GetUInt32(TransformAttributeKeys.TransformAsync) != 0; } catch { }
                        }
                        var enc = new MfH264Encoder(mft, name, hw, async, w, h, fps, bitRate);
                        log?.Add($"selected {name} (hw={hw}, async={async})");
                        return enc;
                    }
                    catch (Exception ex)
                    {
                        log?.Add($"rejected {name} (hw={hw}): {ex.Message}");
                        lastError = ex;
                        mft?.Dispose();
                    }
                }
            }
            finally { foreach (var a in activates) a.Dispose(); }
        }
        throw new PlatformNotSupportedException("No usable H.264 encoder found in Media Foundation.", lastError);
    }

    /// <summary>
    /// Makes the next encoded frame an IDR with SPS/PPS. Applied on the encoding thread just before the next input: sets
    /// CODECAPI_AVEncVideoForceKeyFrame on the MFT and also tags that input sample with MFSampleExtension_VideoEncodePictureType=IDR
    /// (hardware MFTs that ignore the codec property usually honour the sample attribute). SPS/PPS are prepended if the MFT omits them.
    /// </summary>
    public void RequestKeyFrame() => Interlocked.Exchange(ref _keyRequestedAt, Math.Max(1, MediaClock.Now));

    private void ApplyKeyRequest(IMFSample frame)
    {
        long at = Interlocked.Read(ref _keyRequestedAt);
        // The camera stamps a frame with its capture time, which is up to ~50 ms before the frame reaches us. A key frame forced onto
        // a frame captured before the request would carry a timestamp the caller (a resume at that instant) discards, so the request
        // waits for the first frame captured at or after it.
        if (at == 0 || frame.SampleTime < at) return;
        Interlocked.CompareExchange(ref _keyRequestedAt, 0, at);
        _headerWanted = true;
        MfCommon.SetCodecValue(_mft, MfCommon.AvEncVideoForceKeyFrame, 1);
        try { frame.Set(PictureTypeKey, PictureTypeIdr); } catch { }
    }

    /// <summary>Feeds one NV12 frame (takes ownership of <paramref name="frame"/>); encoded access units are delivered through <paramref name="output"/>.</summary>
    public void Encode(IMFSample frame, Action<EncodedSample> output)
    {
        if (_events is null)
        {
            try { ApplyKeyRequest(frame); _mft.ProcessInput(0, frame, 0); }
            finally { frame.Dispose(); }
            Drain(output);
            return;
        }

        PumpEvents(output);
        if (_inputCredits > 0)
        {
            _inputCredits--;
            try { ApplyKeyRequest(frame); _mft.ProcessInput(0, frame, 0); }
            finally { frame.Dispose(); }
        }
        else
        {
            if (_pending is not null) { _pending.Dispose(); DroppedFrames++; }
            _pending = frame;
        }
        PumpEvents(output);
    }

    /// <summary>Drains all remaining output at end of encoding.</summary>
    public void Flush(Action<EncodedSample> output)
    {
        if (_events is null)
        {
            _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            Drain(output);
            return;
        }

        PumpEvents(output);
        if (_pending is not null && _inputCredits > 0) { _inputCredits--; _mft.ProcessInput(0, _pending, 0); _pending.Dispose(); _pending = null; }
        _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        var deadline = Environment.TickCount64 + 2000;
        while (!_drainComplete && Environment.TickCount64 < deadline)
        {
            if (!PumpEvents(output)) Thread.Sleep(2);
        }
    }

    /// <summary>Handles queued async events without blocking. Returns true if any event was processed.</summary>
    private bool PumpEvents(Action<EncodedSample> output)
    {
        bool any = false;
        while (true)
        {
            IMFMediaEvent ev;
            try { ev = _events!.GetEvent(EventFlagNoWait); }
            catch (SharpGenException ex) when (ex.ResultCode.Code == MfCommon.MF_E_NO_EVENTS_AVAILABLE) { return any; }
            any = true;
            using (ev)
            {
                switch ((MediaEventTypes)ev.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        if (_pending is not null)
                        {
                            ApplyKeyRequest(_pending);
                            _mft.ProcessInput(0, _pending, 0);
                            _pending.Dispose();
                            _pending = null;
                        }
                        else _inputCredits++;
                        break;
                    case MediaEventTypes.TransformHaveOutput:
                        Drain(output, once: true);
                        break;
                    case MediaEventTypes.TransformDrainComplete:
                        _drainComplete = true;
                        break;
                }
            }
        }
    }

    private void Drain(Action<EncodedSample> output, bool once = false)
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
                    using var t = _mft.GetOutputAvailableType(0, 0);
                    _mft.SetOutputType(0, t, 0);
                    continue;
                }
                hr.CheckError();
                if (produced is null) return;
                Emit(produced, output);
            }
            finally
            {
                if (own is not null) own.Dispose();
                else produced?.Dispose();
            }
            if (once) return;
        }
    }

    private void Emit(IMFSample sample, Action<EncodedSample> output)
    {
        byte[] data = MfCommon.ReadSample(sample);
        if (data.Length == 0) return;
        long ts = sample.SampleTime;
        var nals = ScanNals(data, out bool hasSps);
        bool key = nals;

        if (_awaitingKey && !key) return;     // drop everything before the first IDR
        if (key && (_awaitingKey || _headerWanted))
        {
            _awaitingKey = false;
            _headerWanted = false;
            if (!hasSps)
            {
                if (_sequenceHeader is null)
                {
                    try
                    {
                        using var cur = _mft.GetOutputCurrentType(0);
                        var hdr = cur.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
                        if (hdr is { Length: > 4 }) _sequenceHeader = hdr;
                    }
                    catch { }
                }
                if (_sequenceHeader is { } sh)
                {
                    var merged = new byte[sh.Length + data.Length];
                    sh.CopyTo(merged, 0);
                    data.CopyTo(merged, sh.Length);
                    data = merged;
                }
            }
        }
        output(new EncodedSample(data, ts, key));
    }

    /// <summary>Scans Annex-B start codes; returns true if an IDR slice (type 5) is present.</summary>
    internal static bool ScanNals(ReadOnlySpan<byte> d, out bool hasSps)
    {
        bool idr = false;
        hasSps = false;
        for (int i = 0; i + 3 < d.Length; i++)
        {
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
            {
                int type = d[i + 3] & 0x1F;
                if (type == 5) idr = true;
                else if (type == 7) hasSps = true;
                i += 3;
            }
        }
        return idr;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending?.Dispose();
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        _events?.Dispose();
        _mft.Dispose();
    }
}
