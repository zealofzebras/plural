using System.Runtime.InteropServices;

namespace Plural.Platform.Apple;

[StructLayout(LayoutKind.Sequential)]
internal struct AudioStreamBasicDescription
{
    public double SampleRate;
    public uint FormatId;
    public uint FormatFlags;
    public uint BytesPerPacket;
    public uint FramesPerPacket;
    public uint BytesPerFrame;
    public uint ChannelsPerFrame;
    public uint BitsPerChannel;
    public uint Reserved;

    public static readonly uint LinearPcm = FourCC.Of("lpcm");
    public static readonly uint Mpeg4Aac = FourCC.Of("aac ");

    private const uint FlagIsFloat = 1, FlagIsSignedInteger = 4, FlagIsPacked = 8;

    public static AudioStreamBasicDescription Pcm16(int sampleRate, int channels) =>
        Pcm(sampleRate, channels, FlagIsSignedInteger | FlagIsPacked, sizeof(short));

    public static AudioStreamBasicDescription Float32(int sampleRate, int channels) =>
        Pcm(sampleRate, channels, FlagIsFloat | FlagIsPacked, sizeof(float));

    private static AudioStreamBasicDescription Pcm(int sampleRate, int channels, uint flags, int bytesPerSample) => new()
    {
        SampleRate = sampleRate,
        FormatId = LinearPcm,
        FormatFlags = flags,
        BytesPerPacket = (uint)(bytesPerSample * channels),
        FramesPerPacket = 1,
        BytesPerFrame = (uint)(bytesPerSample * channels),
        ChannelsPerFrame = (uint)channels,
        BitsPerChannel = (uint)(bytesPerSample * 8),
    };
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AudioQueueBuffer
{
    public uint AudioDataBytesCapacity;
    public void* AudioData;
    public uint AudioDataByteSize;
    public void* UserData;
    public uint PacketDescriptionCapacity;
    public void* PacketDescriptions;
    public uint PacketDescriptionCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AudioBuffer
{
    public uint NumberChannels;
    public uint DataByteSize;
    public void* Data;
}

/// <summary>AudioBufferList with room for one buffer (interleaved data).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioBufferList
{
    public uint NumberBuffers;
    public AudioBuffer Buffer0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioStreamPacketDescription
{
    public long StartOffset;
    public uint VariableFramesInPacket;
    public uint DataByteSize;
}

/// <summary>AudioQueue and AudioConverter (identical C API on macOS and iOS).</summary>
internal static unsafe partial class AT
{
    private const string Lib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    public static readonly uint PropertyIsRunning = FourCC.Of("aqrn");
    /// <summary>kAudioQueueProperty_CurrentDevice: CFStringRef device UID (macOS only).</summary>
    public static readonly uint PropertyCurrentDevice = FourCC.Of("aqcd");
    public static readonly uint ConverterEncodeBitRate = FourCC.Of("brat");
    public static readonly uint ConverterMaximumOutputPacketSize = FourCC.Of("xops");
    /// <summary>kAudioConverterDecompressionMagicCookie.</summary>
    public static readonly uint ConverterDecompressionMagicCookie = FourCC.Of("dmgc");

    [LibraryImport(Lib)]
    public static partial int AudioQueueNewOutput(AudioStreamBasicDescription* format,
        delegate* unmanaged<IntPtr, IntPtr, AudioQueueBuffer*, void> callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode,
        uint flags, IntPtr* queue);

    [LibraryImport(Lib)]
    public static partial int AudioQueueNewInput(AudioStreamBasicDescription* format,
        delegate* unmanaged<IntPtr, IntPtr, AudioQueueBuffer*, IntPtr, uint, IntPtr, void> callback, IntPtr userData, IntPtr runLoop,
        IntPtr runLoopMode, uint flags, IntPtr* queue);

    [LibraryImport(Lib)] public static partial int AudioQueueAllocateBuffer(IntPtr queue, uint size, AudioQueueBuffer** buffer);
    [LibraryImport(Lib)] public static partial int AudioQueueEnqueueBuffer(IntPtr queue, AudioQueueBuffer* buffer, uint descriptions, IntPtr packetDescriptions);
    [LibraryImport(Lib)] public static partial int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [LibraryImport(Lib)] public static partial int AudioQueueStop(IntPtr queue, byte immediate);
    [LibraryImport(Lib)] public static partial int AudioQueueDispose(IntPtr queue, byte immediate);
    [LibraryImport(Lib)] public static partial int AudioQueueSetProperty(IntPtr queue, uint id, void* data, uint size);
    [LibraryImport(Lib)] public static partial int AudioQueueGetProperty(IntPtr queue, uint id, void* data, uint* size);

    [LibraryImport(Lib)]
    public static partial int AudioQueueAddPropertyListener(IntPtr queue, uint id, delegate* unmanaged<IntPtr, IntPtr, uint, void> listener,
        IntPtr userData);

    [LibraryImport(Lib)] public static partial int AudioConverterNew(AudioStreamBasicDescription* source, AudioStreamBasicDescription* destination, IntPtr* converter);
    [LibraryImport(Lib)] public static partial int AudioConverterDispose(IntPtr converter);
    [LibraryImport(Lib)] public static partial int AudioConverterReset(IntPtr converter);
    [LibraryImport(Lib)] public static partial int AudioConverterSetProperty(IntPtr converter, uint id, uint size, void* data);
    [LibraryImport(Lib)] public static partial int AudioConverterGetProperty(IntPtr converter, uint id, uint* size, void* data);

    [LibraryImport(Lib)]
    public static partial int AudioConverterFillComplexBuffer(IntPtr converter,
        delegate* unmanaged<IntPtr, uint*, AudioBufferList*, AudioStreamPacketDescription**, IntPtr, int> inputProc, IntPtr userData,
        uint* outputPackets, AudioBufferList* output, AudioStreamPacketDescription* packetDescriptions);

    public static void Check(int status, string what)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{what} failed ({FourCC.Describe(status)}).");
        }
    }

    /// <summary>Points an AudioQueue at a Core Audio device by UID (macOS; iOS follows the audio session's route instead).</summary>
    public static void SetCurrentDevice(IntPtr queue, string uid)
    {
        var s = CF.CreateString(uid);
        try
        {
            Check(AudioQueueSetProperty(queue, PropertyCurrentDevice, &s, (uint)IntPtr.Size), "Selecting the audio device");
        }
        finally
        {
            CF.CFRelease(s);
        }
    }
}
