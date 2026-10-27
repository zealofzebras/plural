using System.Runtime.InteropServices;

namespace Plural.Platform.Apple;

/// <summary>CMTime (24 bytes). Plural uses timescale 10 000 000 so values are MediaClock ticks.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CMTime
{
    public const uint FlagValid = 1;
    public const int TicksPerSecond = 10_000_000;

    public long Value;
    public int Timescale;
    public uint Flags;
    public long Epoch;

    public static CMTime Invalid => default;

    public static CMTime FromTicks(long ticks) => new() { Value = ticks, Timescale = TicksPerSecond, Flags = FlagValid };

    public static CMTime FromSeconds(int numerator, int denominator) => new() { Value = numerator, Timescale = denominator, Flags = FlagValid };

    public readonly bool IsValid => (Flags & FlagValid) != 0 && Timescale > 0;

    public readonly double Seconds => IsValid ? (double)Value / Timescale : double.NaN;

    /// <summary>The time in 100 ns ticks (0 when invalid).</summary>
    public readonly long ToTicks() => IsValid ? (long)((Int128)Value * TicksPerSecond / Timescale) : 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CMSampleTimingInfo
{
    public CMTime Duration;
    public CMTime PresentationTimeStamp;
    public CMTime DecodeTimeStamp;
}

/// <summary>VTDecompressionOutputCallbackRecord.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VTDecompressionOutputCallbackRecord
{
    public IntPtr Callback;
    public IntPtr RefCon;
}

/// <summary>Exported CFStringRef/CFTypeRef constants (NSString* globals) looked up at runtime; zero when the OS lacks them.</summary>
internal static unsafe class Exports
{
    private static readonly Dictionary<string, IntPtr> Libraries = [];

    public static IntPtr Library(string path)
    {
        lock (Libraries)
        {
            if (!Libraries.TryGetValue(path, out var handle))
            {
                handle = NativeLibrary.Load(path);
                Libraries[path] = handle;
            }
            return handle;
        }
    }

    /// <summary>Value of a global pointer variable (e.g. an NSString* constant); zero if the symbol does not exist.</summary>
    public static IntPtr Constant(string library, string name) =>
        NativeLibrary.TryGetExport(Library(library), name, out var address) ? *(IntPtr*)address : IntPtr.Zero;

    /// <summary>Address of a global symbol (e.g. a struct variable).</summary>
    public static IntPtr Address(string library, string name) =>
        NativeLibrary.TryGetExport(Library(library), name, out var address) ? address : IntPtr.Zero;
}

/// <summary>CoreFoundation collections and numbers used for VideoToolbox/AVFoundation settings.</summary>
internal static unsafe partial class CFx
{
    private const string Lib = CF.Lib;
    public const int NumberSInt32 = 3;
    public const int NumberSInt64 = 4;
    public const int NumberDouble = 13;

    [LibraryImport(Lib)] public static partial IntPtr CFDictionaryCreateMutable(IntPtr alloc, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [LibraryImport(Lib)] public static partial void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);
    [LibraryImport(Lib)] public static partial IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [LibraryImport(Lib)] public static partial IntPtr CFNumberCreate(IntPtr alloc, nint type, void* value);
    [LibraryImport(Lib)] public static partial nint CFArrayGetCount(IntPtr array);
    [LibraryImport(Lib)] public static partial IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [LibraryImport(Lib)] public static partial byte CFBooleanGetValue(IntPtr boolean);

    private static readonly Lazy<IntPtr> KeyCallBacks = new(() => Exports.Address(Lib, "kCFTypeDictionaryKeyCallBacks"));
    private static readonly Lazy<IntPtr> ValueCallBacks = new(() => Exports.Address(Lib, "kCFTypeDictionaryValueCallBacks"));
    private static readonly Lazy<IntPtr> TrueValue = new(() => Exports.Constant(Lib, "kCFBooleanTrue"));
    private static readonly Lazy<IntPtr> FalseValue = new(() => Exports.Constant(Lib, "kCFBooleanFalse"));

    public static IntPtr True => TrueValue.Value;
    public static IntPtr False => FalseValue.Value;

    /// <summary>New mutable CFDictionary with CFType callbacks (+1; CFRelease it).</summary>
    public static IntPtr CreateDictionary() => CFDictionaryCreateMutable(IntPtr.Zero, 0, KeyCallBacks.Value, ValueCallBacks.Value);

    /// <summary>Sets <paramref name="key"/> (ignored when the key constant is missing on this OS).</summary>
    public static void Set(IntPtr dict, IntPtr key, IntPtr value)
    {
        if (key != IntPtr.Zero && value != IntPtr.Zero)
        {
            CFDictionarySetValue(dict, key, value);
        }
    }

    public static void Set(IntPtr dict, IntPtr key, int value)
    {
        var n = Number(value);
        Set(dict, key, n);
        CF.CFRelease(n);
    }

    public static IntPtr Number(int value) => CFNumberCreate(IntPtr.Zero, NumberSInt32, &value);

    public static IntPtr Number(double value) => CFNumberCreate(IntPtr.Zero, NumberDouble, &value);
}

internal static unsafe partial class CM
{
    public const string Lib = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";

    [LibraryImport(Lib)] public static partial IntPtr CMSampleBufferGetImageBuffer(IntPtr sbuf);
    [LibraryImport(Lib)] public static partial CMTime CMSampleBufferGetPresentationTimeStamp(IntPtr sbuf);
    [LibraryImport(Lib)] public static partial IntPtr CMSampleBufferGetDataBuffer(IntPtr sbuf);
    [LibraryImport(Lib)] public static partial IntPtr CMSampleBufferGetFormatDescription(IntPtr sbuf);
    [LibraryImport(Lib)] public static partial IntPtr CMSampleBufferGetSampleAttachmentsArray(IntPtr sbuf, byte createIfNecessary);
    [LibraryImport(Lib)] public static partial nuint CMBlockBufferGetDataLength(IntPtr buffer);
    [LibraryImport(Lib)] public static partial int CMBlockBufferCopyDataBytes(IntPtr buffer, nuint offset, nuint length, void* destination);
    [LibraryImport(Lib)] public static partial int CMBlockBufferReplaceDataBytes(void* source, IntPtr buffer, nuint offset, nuint length);
    [LibraryImport(Lib)] public static partial int CMBlockBufferCreateWithMemoryBlock(IntPtr alloc, void* memoryBlock, nuint blockLength,
        IntPtr blockAllocator, void* customBlockSource, nuint offsetToData, nuint dataLength, uint flags, IntPtr* blockBufferOut);
    [LibraryImport(Lib)] public static partial int CMSampleBufferCreateReady(IntPtr alloc, IntPtr dataBuffer, IntPtr formatDescription,
        nint numSamples, nint numSampleTimingEntries, CMSampleTimingInfo* timing, nint numSampleSizeEntries, nuint* sizes, IntPtr* sampleBufferOut);
    [LibraryImport(Lib)] public static partial int CMVideoFormatDescriptionGetH264ParameterSetAtIndex(IntPtr desc, nuint index,
        byte** parameterSet, nuint* size, nuint* count, int* nalUnitHeaderLength);
    [LibraryImport(Lib)] public static partial int CMVideoFormatDescriptionCreateFromH264ParameterSets(IntPtr alloc, nuint count,
        byte** parameterSets, nuint* sizes, int nalUnitHeaderLength, IntPtr* formatDescriptionOut);
    [LibraryImport(Lib)] public static partial IntPtr CMClockGetHostTimeClock();
    [LibraryImport(Lib)] public static partial CMTime CMClockGetTime(IntPtr clock);

    private static readonly Lazy<IntPtr> NotSyncKey = new(() => Exports.Constant(Lib, "kCMSampleAttachmentKey_NotSync"));

    /// <summary>True unless the sample's attachments mark it kCMSampleAttachmentKey_NotSync.</summary>
    public static bool IsSyncSample(IntPtr sbuf)
    {
        var attachments = CMSampleBufferGetSampleAttachmentsArray(sbuf, 0);
        if (attachments == IntPtr.Zero || CFx.CFArrayGetCount(attachments) == 0)
        {
            return true;
        }
        var dict = CFx.CFArrayGetValueAtIndex(attachments, 0);
        var notSync = dict == IntPtr.Zero ? IntPtr.Zero : CFx.CFDictionaryGetValue(dict, NotSyncKey.Value);
        return notSync == IntPtr.Zero || CFx.CFBooleanGetValue(notSync) == 0;
    }

    /// <summary>Host time (mach absolute time, the clock capture timestamps use) in 100 ns ticks.</summary>
    public static long HostTicks() => CMClockGetTime(CMClockGetHostTimeClock()).ToTicks();
}

internal static unsafe partial class CV
{
    public const string Lib = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    public const ulong LockReadOnly = 1;
    public static readonly uint PixelFormatBgra = FourCC.Of("BGRA");

    [LibraryImport(Lib)] public static partial int CVPixelBufferLockBaseAddress(IntPtr buffer, ulong flags);
    [LibraryImport(Lib)] public static partial int CVPixelBufferUnlockBaseAddress(IntPtr buffer, ulong flags);
    [LibraryImport(Lib)] public static partial IntPtr CVPixelBufferGetBaseAddress(IntPtr buffer);
    [LibraryImport(Lib)] public static partial nuint CVPixelBufferGetBytesPerRow(IntPtr buffer);
    [LibraryImport(Lib)] public static partial nuint CVPixelBufferGetWidth(IntPtr buffer);
    [LibraryImport(Lib)] public static partial nuint CVPixelBufferGetHeight(IntPtr buffer);
    [LibraryImport(Lib)] public static partial uint CVPixelBufferGetPixelFormatType(IntPtr buffer);

    private static readonly Lazy<IntPtr> PixelFormatKey = new(() => Exports.Constant(Lib, "kCVPixelBufferPixelFormatTypeKey"));
    private static readonly Lazy<IntPtr> WidthKey = new(() => Exports.Constant(Lib, "kCVPixelBufferWidthKey"));
    private static readonly Lazy<IntPtr> HeightKey = new(() => Exports.Constant(Lib, "kCVPixelBufferHeightKey"));

    /// <summary>Pixel buffer attributes asking for BGRA, optionally scaled to <paramref name="width"/>×<paramref name="height"/> (+1).</summary>
    public static IntPtr BgraAttributes(int width = 0, int height = 0)
    {
        var dict = CFx.CreateDictionary();
        CFx.Set(dict, PixelFormatKey.Value, unchecked((int)PixelFormatBgra));
        if (width > 0 && height > 0)
        {
            CFx.Set(dict, WidthKey.Value, width);
            CFx.Set(dict, HeightKey.Value, height);
        }
        return dict;
    }

    /// <summary>
    /// Copies a locked BGRA buffer into <paramref name="dst"/> (tightly packed <paramref name="dw"/>×<paramref name="dh"/>), nearest-neighbour scaled.
    /// </summary>
    public static void ScaleBgra(byte* src, int srcStride, int sw, int sh, byte[] dst, int dw, int dh)
    {
        fixed (byte* d0 = dst)
        {
            var o = (uint*)d0;
            for (var y = 0; y < dh; y++)
            {
                var row = (uint*)(src + (long)(y * sh / dh) * srcStride);
                if (dw == sw)
                {
                    Buffer.MemoryCopy(row, o, (long)dw * 4, (long)dw * 4);
                    o += dw;
                    continue;
                }
                for (var x = 0; x < dw; x++)
                {
                    *o++ = row[x * sw / dw];
                }
            }
        }
    }
}

internal static unsafe partial class VT
{
    public const string Lib = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";
    public const uint EncodeInfoFrameDropped = 1 << 1;
    public const int InvalidSessionErr = -12903;
    public static readonly uint CodecH264 = FourCC.Of("avc1");

    [LibraryImport(Lib)] public static partial int VTCompressionSessionCreate(IntPtr alloc, int width, int height, uint codecType,
        IntPtr encoderSpecification, IntPtr sourceImageBufferAttributes, IntPtr compressedDataAllocator,
        delegate* unmanaged<IntPtr, IntPtr, int, uint, IntPtr, void> outputCallback, IntPtr outputCallbackRefCon, IntPtr* sessionOut);
    [LibraryImport(Lib)] public static partial int VTSessionSetProperty(IntPtr session, IntPtr key, IntPtr value);
    [LibraryImport(Lib)] public static partial int VTCompressionSessionPrepareToEncodeFrames(IntPtr session);
    [LibraryImport(Lib)] public static partial int VTCompressionSessionEncodeFrame(IntPtr session, IntPtr imageBuffer, CMTime pts, CMTime duration,
        IntPtr frameProperties, IntPtr sourceFrameRefCon, uint* infoFlagsOut);
    [LibraryImport(Lib)] public static partial int VTCompressionSessionCompleteFrames(IntPtr session, CMTime completeUntil);
    [LibraryImport(Lib)] public static partial void VTCompressionSessionInvalidate(IntPtr session);

    [LibraryImport(Lib)] public static partial int VTDecompressionSessionCreate(IntPtr alloc, IntPtr formatDescription, IntPtr decoderSpecification,
        IntPtr destinationImageBufferAttributes, VTDecompressionOutputCallbackRecord* callback, IntPtr* sessionOut);
    [LibraryImport(Lib)] public static partial int VTDecompressionSessionDecodeFrame(IntPtr session, IntPtr sampleBuffer, uint flags,
        IntPtr sourceFrameRefCon, uint* infoFlagsOut);
    [LibraryImport(Lib)] public static partial int VTDecompressionSessionFinishDelayedFrames(IntPtr session);
    [LibraryImport(Lib)] public static partial int VTDecompressionSessionWaitForAsynchronousFrames(IntPtr session);
    [LibraryImport(Lib)] public static partial void VTDecompressionSessionInvalidate(IntPtr session);

    public static IntPtr Key(string name) => Exports.Constant(Lib, name);

    public static void Check(int status, string what)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{what} failed (VideoToolbox {status}).");
        }
    }
}

/// <summary>libdispatch (C API; no blocks needed).</summary>
internal static unsafe partial class Dispatch
{
    private const string Lib = "/usr/lib/libSystem.B.dylib";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr dispatch_queue_create(string label, IntPtr attr);
    [LibraryImport(Lib)] public static partial void dispatch_release(IntPtr obj);
    [LibraryImport(Lib)] public static partial void dispatch_sync_f(IntPtr queue, IntPtr context, delegate* unmanaged<IntPtr, void> work);

    [UnmanagedCallersOnly]
    private static void Nothing(IntPtr context)
    {
    }

    /// <summary>Returns once everything already queued on the serial <paramref name="queue"/> has run.</summary>
    public static void Drain(IntPtr queue) => dispatch_sync_f(queue, IntPtr.Zero, &Nothing);
}
