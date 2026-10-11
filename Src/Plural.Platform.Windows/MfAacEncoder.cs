using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Platform;
using Vortice.MediaFoundation;

namespace Plural.Platform.Windows;

/// <summary>Creates AAC-LC encoders on the Microsoft Media Foundation AAC encoder MFT.</summary>
[SupportedOSPlatform("windows")]
public sealed class MfAacEncoderFactory : IAacEncoderFactory
{
    public IAacEncoder Create(int sampleRate, int channels, int bitRate) => new MfAacEncoder(sampleRate, channels, bitRate);
}

/// <summary>
/// Synchronous AAC-LC encoder (raw payload, no ADTS). Input 16-bit PCM. Output timestamps are derived from the number of
/// input samples (starting at 0, 100 ns ticks), i.e. relative to the first PCM sample fed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MfAacEncoder : IAacEncoder
{
    // The Microsoft AAC encoder only supports these average rates (bytes/s): 96, 128, 160, 192 kbps.
    private static readonly int[] SupportedBytesPerSecond = [12000, 16000, 20000, 24000];

    private readonly IMFTransform _mft;
    private readonly int _outputBufferSize;
    private readonly bool _mftProvidesSamples;
    private long _inputSamples;       // per channel
    private long _outputFrames;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public ReadOnlyMemory<byte> AudioSpecificConfig { get; }

    /// <summary>Bit rate actually configured (bits/s), the closest supported to the requested one.</summary>
    public int ActualBitRate { get; }

    public MfAacEncoder(int sampleRate, int channels, int requestedBitRate)
    {
        SampleRate = sampleRate;
        Channels = channels;
        MfCommon.EnsureStarted();

        int wanted = requestedBitRate / 8;
        int bytesPerSec = SupportedBytesPerSecond.OrderBy(b => Math.Abs(b - wanted)).First();
        ActualBitRate = bytesPerSec * 8;

        _mft = CreateMft();

        // Output type first (the AAC encoder requires it before the input type).
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            outType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
            outType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)bytesPerSec);
            outType.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);                     // raw_data_block
            outType.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);  // AAC-LC L2
            _mft.SetOutputType(0, outType, 0);
        }

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
            inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(2 * channels));
            inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(2 * channels * sampleRate));
            _mft.SetInputType(0, inType, 0);
        }

        AudioSpecificConfig = ReadAudioSpecificConfig(sampleRate, channels);

        var info = _mft.GetOutputStreamInfo(0);
        _outputBufferSize = Math.Max(info.Size, 4096);
        _mftProvidesSamples = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private static IMFTransform CreateMft()
    {
        var activates = MfCommon.EnumTransforms(TransformCategoryGuids.AudioEncoder, MfCommon.EnumSync | MfCommon.EnumSortAndFilter,
            null, AudioFormatGuids.Aac, MediaTypeGuids.Audio);
        try
        {
            if (activates.Count == 0) throw new PlatformNotSupportedException("No AAC encoder found in Media Foundation.");
            return MfCommon.ActivateTransform(activates[0]);
        }
        finally { foreach (var a in activates) a.Dispose(); }
    }

    private ReadOnlyMemory<byte> ReadAudioSpecificConfig(int sampleRate, int channels)
    {
        try
        {
            using var t = _mft.GetOutputCurrentType(0);
            byte[] blob = t.GetBlob(MediaTypeAttributeKeys.UserData);
            // HEAACWAVEINFO prefix: wPayloadType, wAudioProfileLevelIndication, wStructType (2 bytes each), wReserved1, dwReserved2 = 12 bytes.
            if (blob.Length > 12) return blob.AsMemory(12);
        }
        catch { /* fall through to computed config */ }

        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int idx = Array.IndexOf(rates, sampleRate);
        if (idx < 0) idx = 4;
        int v = (2 << 11) | (idx << 7) | (channels << 3);
        return new byte[] { (byte)(v >> 8), (byte)v };
    }

    public unsafe void Encode(ReadOnlySpan<short> pcm, Action<EncodedSample> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm.IsEmpty) return;

        var bytes = MemoryMarshal.AsBytes(pcm);
        using var sample = MfCommon.CreateSample(bytes.Length);
        using (var buf = sample.GetBufferByIndex(0))
        {
            buf.Lock(out IntPtr ptr, out _, out _);
            try { bytes.CopyTo(new Span<byte>((void*)ptr, bytes.Length)); }
            finally { buf.Unlock(); }
            buf.CurrentLength = bytes.Length;
        }
        long frames = pcm.Length / Channels;
        sample.SampleTime = _inputSamples * 10_000_000L / SampleRate;
        sample.SampleDuration = frames * 10_000_000L / SampleRate;

        // Drain pending output first so the MFT always accepts input.
        Drain(output);
        _mft.ProcessInput(0, sample, 0);
        Drain(output);
        _inputSamples += frames;
    }

    public void Flush(Action<EncodedSample> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        Drain(output);
    }

    private unsafe void Drain(Action<EncodedSample> output)
    {
        while (true)
        {
            IMFSample? own = _mftProvidesSamples ? null : MfCommon.CreateSample(_outputBufferSize);
            var buffer = new OutputDataBuffer { StreamID = 0, Sample = own };
            var hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            try
            {
                if (hr.Code == MfCommon.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                hr.CheckError();
                var produced = buffer.Sample;
                if (produced is null) return;
                byte[] data = MfCommon.ReadSample(produced);
                long ts = _outputFrames * 1024L * 10_000_000L / SampleRate;
                _outputFrames++;
                if (data.Length > 0) output(new EncodedSample(data, ts, true));
            }
            finally
            {
                if (own is not null) own.Dispose();
                else buffer.Sample?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        _mft.Dispose();
    }
}
