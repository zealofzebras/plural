using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Core.Platform;

namespace Plural.Platform.Apple;

/// <summary>Creates AAC-LC and MP3 decoders on AudioToolbox's AudioConverter (16-bit interleaved PCM out; macOS and iOS).</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
public sealed class AppleAudioDecoderFactory : IAudioDecoderFactory
{
    private readonly Dictionary<string, bool> _can = [];

    public bool CanDecode(string codec)
    {
        var c = codec.ToLowerInvariant();
        if (c is not ("aac" or "mp3"))
        {
            return false;
        }
        lock (_can)
        {
            if (_can.TryGetValue(c, out var known))
            {
                return known;
            }
            bool ok;
            try
            {
                using var d = new AppleAudioDecoder(c, 44100, 1, default);
                ok = true;
            }
            catch (Exception)
            {
                ok = false;
            }
            return _can[c] = ok;
        }
    }

    public IAudioDecoder Create(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate)
    {
        var c = codec.ToLowerInvariant();
        if (c is not ("aac" or "mp3"))
        {
            throw new NotSupportedException($"Codec '{codec}' is not supported.");
        }
        try
        {
            return new AppleAudioDecoder(c, sampleRate, channels, codecPrivate);
        }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            throw new NotSupportedException($"AudioToolbox could not create a {codec} decoder: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Synchronous AudioConverter decoder. Each packet is copied to native memory and handed to the converter by the input callback
/// (one packet with its <see cref="AudioStreamPacketDescription"/>); the callback answers "no data yet" on the converter's next
/// request, which ends that FillComplexBuffer call with whatever PCM the packet produced.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios")]
internal sealed unsafe class AppleAudioDecoder : IAudioDecoder
{
    private const int MaxFramesPerCall = 8192; // HE-AAC (SBR) packets decode to 2048 frames; MP3 to 1152.
    private const uint ObjectAacLc = 2;
    private static readonly int NoDataYet = (int)FourCC.Of("noda");
    private static readonly uint Mp3Format = FourCC.Of(".mp3");

    private IntPtr _converter;
    private GCHandle _self;
    private short* _pcm;
    private byte* _packet;
    private int _packetCapacity;
    private int _packetLength;
    private bool _supplied = true;
    private AudioStreamPacketDescription* _description;
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }

    public AppleAudioDecoder(string codec, int sampleRate, int channels, ReadOnlyMemory<byte> codecPrivate)
    {
        SampleRate = sampleRate;
        Channels = channels;
        var aac = codec == "aac";
        var source = new AudioStreamBasicDescription
        {
            SampleRate = sampleRate,
            FormatId = aac ? AudioStreamBasicDescription.Mpeg4Aac : Mp3Format,
            FormatFlags = aac ? ObjectAacLc : 0,
            FramesPerPacket = aac ? 1024u : (sampleRate >= 32000 ? 1152u : 576u),
            ChannelsPerFrame = (uint)channels,
        };
        var destination = AudioStreamBasicDescription.Pcm16(sampleRate, channels);
        IntPtr converter;
        AT.Check(AT.AudioConverterNew(&source, &destination, &converter), $"Creating the {codec} decoder");
        _converter = converter;
        try
        {
            if (aac)
            {
                SetAacCookie(codecPrivate.Length > 0 ? codecPrivate.Span : AppleAacEncoder.ComputeConfig(sampleRate, channels));
            }
            _pcm = (short*)NativeMemory.Alloc((nuint)(MaxFramesPerCall * channels), sizeof(short));
            _description = (AudioStreamPacketDescription*)NativeMemory.AllocZeroed((nuint)sizeof(AudioStreamPacketDescription));
            _self = GCHandle.Alloc(this);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Apple's AAC decoder takes the cookie in the form an MP4 'esds' atom holds it: an MPEG-4 ES_Descriptor (tag 0x03) wrapping a
    /// DecoderConfigDescriptor (0x04) whose DecoderSpecificInfo (0x05) is the AudioSpecificConfig. If that is rejected the bare
    /// AudioSpecificConfig is tried.
    /// </summary>
    private void SetAacCookie(ReadOnlySpan<byte> audioSpecificConfig)
    {
        var esds = BuildEsds(audioSpecificConfig);
        int status;
        fixed (byte* p = esds)
        {
            status = AT.AudioConverterSetProperty(_converter, AT.ConverterDecompressionMagicCookie, (uint)esds.Length, p);
        }
        if (status != 0)
        {
            fixed (byte* p = audioSpecificConfig)
            {
                status = AT.AudioConverterSetProperty(_converter, AT.ConverterDecompressionMagicCookie, (uint)audioSpecificConfig.Length, p);
            }
        }
        AT.Check(status, "Setting the AAC decoder configuration");
    }

    /// <summary>ES_Descriptor { ES_ID 0, flags 0, DecoderConfig { objectType 0x40 (AAC), streamType 5 (audio), DecoderSpecificInfo = ASC }, SLConfig 2 }.</summary>
    internal static byte[] BuildEsds(ReadOnlySpan<byte> asc)
    {
        var ascLength = LengthBytes(asc.Length);
        var decoderConfigLength = 13 + 1 + ascLength + asc.Length;   // type, stream, buffer size(3), max rate(4), avg rate(4) + 0x05 + len + ASC
        var configLengthBytes = LengthBytes(decoderConfigLength);
        var esLength = 3 + 1 + configLengthBytes + decoderConfigLength + 3;   // ES_ID(2) + flags(1) + 0x04 + len + config + SLConfig (06 01 02)
        var list = new List<byte> { 0x03 };
        AddLength(list, esLength);
        list.AddRange([0x00, 0x00, 0x00]);
        list.Add(0x04);
        AddLength(list, decoderConfigLength);
        list.AddRange([0x40, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        list.Add(0x05);
        AddLength(list, asc.Length);
        list.AddRange(asc.ToArray());
        list.AddRange([0x06, 0x01, 0x02]);
        return [.. list];

        static int LengthBytes(int n) => n < 0x80 ? 1 : 2;

        // Descriptor lengths are 7 bits per byte, big-endian, with the top bit set on all but the last byte (values below 16384 here).
        static void AddLength(List<byte> l, int n)
        {
            if (n < 0x80)
            {
                l.Add((byte)n);
            }
            else
            {
                l.Add((byte)(0x80 | (n >> 7)));
                l.Add((byte)(n & 0x7F));
            }
        }
    }

    public void Decode(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<short>> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packet.IsEmpty)
        {
            return;
        }
        if (_packetCapacity < packet.Length)
        {
            NativeMemory.Free(_packet);
            _packet = (byte*)NativeMemory.Alloc((nuint)packet.Length);
            _packetCapacity = packet.Length;
        }
        packet.CopyTo(new Span<byte>(_packet, packet.Length));
        _packetLength = packet.Length;
        _supplied = false;
        Pull(output);
    }

    public void Drain(Action<ReadOnlySpan<short>> output)
    {
        // AAC-LC and MP3 hold no PCM beyond what the last packet produced; nothing to flush.
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AT.Check(AT.AudioConverterReset(_converter), "Resetting the audio decoder");
        _supplied = true;
    }

    private void Pull(Action<ReadOnlySpan<short>> output)
    {
        var list = new AudioBufferList
        {
            NumberBuffers = 1,
            Buffer0 = new AudioBuffer
            {
                NumberChannels = (uint)Channels,
                DataByteSize = (uint)(MaxFramesPerCall * Channels * sizeof(short)),
                Data = _pcm,
            },
        };
        uint frames = MaxFramesPerCall;
        var status = AT.AudioConverterFillComplexBuffer(_converter, &Supply, GCHandle.ToIntPtr(_self), &frames, &list, null);
        if (status != 0 && status != NoDataYet)
        {
            AT.Check(status, "Audio decoding");
        }
        if (frames > 0)
        {
            output(new ReadOnlySpan<short>(_pcm, (int)frames * Channels));
        }
    }

    [UnmanagedCallersOnly]
    private static int Supply(IntPtr converter, uint* packets, AudioBufferList* data, AudioStreamPacketDescription** descriptions, IntPtr user)
    {
        try
        {
            return ((AppleAudioDecoder)GCHandle.FromIntPtr(user).Target!).Supply(packets, data, descriptions);
        }
        catch (Exception)
        {
            *packets = 0;
            return NoDataYet;
        }
    }

    private int Supply(uint* packets, AudioBufferList* data, AudioStreamPacketDescription** descriptions)
    {
        data->NumberBuffers = 1;
        data->Buffer0.NumberChannels = (uint)Channels;
        if (_supplied)
        {
            *packets = 0;
            data->Buffer0.DataByteSize = 0;
            data->Buffer0.Data = null;
            return NoDataYet;
        }
        _supplied = true;
        data->Buffer0.DataByteSize = (uint)_packetLength;
        data->Buffer0.Data = _packet;
        *packets = 1;
        if (descriptions != null)
        {
            _description->StartOffset = 0;
            _description->VariableFramesInPacket = 0;
            _description->DataByteSize = (uint)_packetLength;
            *descriptions = _description;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_converter != IntPtr.Zero)
        {
            AT.AudioConverterDispose(_converter);
            _converter = IntPtr.Zero;
        }
        NativeMemory.Free(_pcm);
        NativeMemory.Free(_packet);
        NativeMemory.Free(_description);
        _pcm = null;
        _packet = null;
        _description = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
