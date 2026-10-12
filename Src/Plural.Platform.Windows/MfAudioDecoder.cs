using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpGen.Runtime;
using Plural.Core.Platform;
using Vortice.MediaFoundation;

namespace Plural.Platform.Windows;

/// <summary>Creates AAC-LC and MP3 decoders on the Microsoft Media Foundation decoder MFTs (16-bit PCM out).</summary>
[SupportedOSPlatform("windows")]
public sealed class MfAudioDecoderFactory : IAudioDecoderFactory
{
    private readonly Dictionary<string, bool> _can = [];

    public bool CanDecode(string codec)
    {
        string c = codec.ToLowerInvariant();
        if (c is not ("aac" or "mp3")) return false;
        lock (_can)
        {
            if (_can.TryGetValue(c, out bool known)) return known;
            bool ok;
            try
            {
                using var d = MfAudioDecoder.Create(c, 44100, 1, c == "aac" ? new byte[] { 0x12, 0x08 } : default);
                ok = true;
            }
            catch { ok = false; }
            return _can[c] = ok;
        }
    }

    public IAudioDecoder Create(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate)
    {
        string c = codec.ToLowerInvariant();
        if (c is not ("aac" or "mp3")) throw new NotSupportedException($"Codec '{codec}' is not supported.");
        try { return MfAudioDecoder.Create(c, sampleRate, channels, codecPrivate); }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            throw new NotSupportedException($"Media Foundation could not create a {codec} decoder: {ex.Message}", ex);
        }
    }
}

/// <summary>Synchronous Media Foundation audio decoder (AAC raw frames or MP3 frames → interleaved 16-bit PCM).</summary>
[SupportedOSPlatform("windows")]
internal sealed class MfAudioDecoder : IAudioDecoder
{
    private readonly IMFTransform _mft;
    private int _outSize;
    private bool _provides;
    private bool _disposed;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }

    private MfAudioDecoder(IMFTransform mft) { _mft = mft; }

    public static MfAudioDecoder Create(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate)
    {
        MfCommon.EnsureStarted();
        Guid subtype = codec == "aac" ? AudioFormatGuids.Aac : AudioFormatGuids.Mp3;
        var activates = MfCommon.EnumTransforms(TransformCategoryGuids.AudioDecoder, MfCommon.EnumSync | MfCommon.EnumSortAndFilter,
            subtype, null, MediaTypeGuids.Audio);
        try
        {
            if (activates.Count == 0) throw new PlatformNotSupportedException($"No {codec} decoder found in Media Foundation.");
            Exception? last = null;
            foreach (var act in activates)
            {
                IMFTransform? mft = null;
                try
                {
                    mft = MfCommon.ActivateTransform(act);
                    var dec = new MfAudioDecoder(mft);
                    dec.Configure(codec, subtype, sampleRate, channels, codecPrivate.Span);
                    return dec;
                }
                catch (Exception ex) { last = ex; mft?.Dispose(); }
            }
            throw new PlatformNotSupportedException($"No usable {codec} decoder.", last);
        }
        finally { foreach (var a in activates) a.Dispose(); }
    }

    private void Configure(string codec, Guid subtype, int sampleRate, int channels, ReadOnlySpan<byte> config)
    {
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            inType.Set(MediaTypeAttributeKeys.Subtype, subtype);
            inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
            inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            if (codec == "aac")
            {
                inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                inType.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);                      // raw_data_block
                inType.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);   // AAC-LC L2
                // HEAACWAVEINFO: wPayloadType, wAudioProfileLevelIndication, wStructType, wReserved1, dwReserved2, then the ASC.
                var blob = new byte[12 + config.Length];
                blob[2] = 0x29;   // profile/level (little endian word at offset 2)
                config.CopyTo(blob.AsSpan(12));
                inType.SetBlob(MediaTypeAttributeKeys.UserData, blob);
            }
            else
            {
                inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 1u);
            }
            _mft.SetInputType(0, inType, 0);
        }

        SelectPcmOutput(sampleRate, channels);
        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>Picks the 16-bit PCM output type the MFT offers (closest to the input rate/channels).</summary>
    private void SelectPcmOutput(int wantRate, int wantChannels)
    {
        IMFMediaType? best = null;
        int bestScore = int.MaxValue;
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _mft.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { break; }
            bool keep = false;
            try
            {
                if (t.GetGUID(MediaTypeAttributeKeys.Subtype) != AudioFormatGuids.Pcm) continue;
                if (t.GetUInt32(MediaTypeAttributeKeys.AudioBitsPerSample) != 16) continue;
                int rate = (int)t.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
                int ch = (int)t.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
                int score = Math.Abs(rate - wantRate) + Math.Abs(ch - wantChannels) * 100000;
                if (score < bestScore) { best?.Dispose(); best = t; bestScore = score; keep = true; }
            }
            catch (SharpGenException) { }
            finally { if (!keep) t.Dispose(); }
        }
        if (best is null) throw new PlatformNotSupportedException("Decoder offers no 16-bit PCM output.");
        using (best)
        {
            _mft.SetOutputType(0, best, 0);
            SampleRate = (int)best.GetUInt32(MediaTypeAttributeKeys.AudioSamplesPerSecond);
            Channels = (int)best.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels);
        }
        var info = _mft.GetOutputStreamInfo(0);
        _provides = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        _outSize = Math.Max(info.Size, 16384);
    }

    public unsafe void Decode(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<short>> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packet.IsEmpty) return;
        Pull(output);
        using var sample = MfCommon.CreateSample(packet.Length);
        using (var buf = sample.GetBufferByIndex(0))
        {
            buf.Lock(out IntPtr ptr, out _, out _);
            try { packet.CopyTo(new Span<byte>((void*)ptr, packet.Length)); }
            finally { buf.Unlock(); }
            buf.CurrentLength = packet.Length;
        }
        _mft.ProcessInput(0, sample, 0);
        Pull(output);
    }

    public void Drain(Action<ReadOnlySpan<short>> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        Pull(output);
        _mft.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mft.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private void Pull(Action<ReadOnlySpan<short>> output)
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
                    SelectPcmOutput(SampleRate, Channels);
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
        }
    }

    private void Emit(IMFSample sample, Action<ReadOnlySpan<short>> output)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr ptr, out _, out int len);
        try
        {
            int n = len / 2;
            if (n == 0) return;
            unsafe { output(new ReadOnlySpan<short>((void*)ptr, n)); }
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
