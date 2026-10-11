using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vortice.MediaFoundation;

namespace SmartMike.Platform.Windows;

/// <summary>Media Foundation process-wide startup and small interop helpers shared by the AAC and video code.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class MfCommon
{
    private static readonly object Gate = new();
    private static bool _started;

    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);

    // MFTEnumEx flags
    public const uint EnumSync = 0x1, EnumAsync = 0x2, EnumHardware = 0x4, EnumSortAndFilter = 0x40;

    public static readonly Guid IID_IMFTransform = typeof(IMFTransform).GUID;
    public static readonly Guid IID_IMFMediaSource = typeof(IMFMediaSource).GUID;

    /// <summary>MFStartup once for the process (never shut down; MF is cheap to keep alive).</summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started) return;
            MediaFactory.MFStartup();
            _started = true;
        }
    }

    /// <summary>Enumerates MFTs of a category as activation objects. Caller disposes each activate.</summary>
    public static List<IMFActivate> EnumTransforms(Guid category, uint flags, Guid? inputSubtype, Guid? outputSubtype, Guid majorType)
    {
        EnsureStarted();
        RegisterTypeInfo? input = inputSubtype is { } i ? new RegisterTypeInfo { GuidMajorType = majorType, GuidSubtype = i } : null;
        RegisterTypeInfo? output = outputSubtype is { } o ? new RegisterTypeInfo { GuidMajorType = majorType, GuidSubtype = o } : null;
        MediaFactory.MFTEnumEx(category, flags, input, output, out IntPtr array, out uint count);
        return ReadActivates(array, count);
    }

    public static List<IMFActivate> ReadActivates(IntPtr array, uint count)
    {
        var list = new List<IMFActivate>((int)count);
        if (array == IntPtr.Zero) return list;
        for (int n = 0; n < count; n++)
        {
            IntPtr p = Marshal.ReadIntPtr(array, n * IntPtr.Size);
            if (p != IntPtr.Zero) list.Add(new IMFActivate(p));
        }
        Marshal.FreeCoTaskMem(array);
        return list;
    }

    public static IMFTransform ActivateTransform(IMFActivate activate)
    {
        activate.ActivateObject<IMFTransform>(IID_IMFTransform, out var t);
        return t!;
    }

    /// <summary>Allocates a system-memory sample with one buffer of the given size.</summary>
    public static IMFSample CreateSample(int size)
    {
        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(MediaFactory.MFCreateMemoryBuffer(size));
        return sample;
    }

    /// <summary>Copies the sample's payload into a managed array.</summary>
    public static byte[] ReadSample(IMFSample sample)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr ptr, out _, out int len);
        try
        {
            var data = new byte[len];
            Marshal.Copy(ptr, data, 0, len);
            return data;
        }
        finally { buffer.Unlock(); }
    }

    // ---- ICodecAPI (not wrapped by Vortice): minimal vtable calls on the MFT's native pointer ----

    private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static readonly Guid AvEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AvEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AvEncCommonLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
    public static readonly Guid AvEncMpvGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AvEncMpvDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AvEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint UInt;
    }

    private const ushort VT_UI4 = 19, VT_BOOL = 11;

    /// <summary>Best-effort ICodecAPI.SetValue; returns the HRESULT (negative = failure, e.g. unsupported).</summary>
    public static int SetCodecValue(IMFTransform transform, Guid api, uint value, bool isBool = false)
    {
        IntPtr unk = transform.NativePointer;
        Guid iid = IID_ICodecAPI;
        int hr = Marshal.QueryInterface(unk, in iid, out IntPtr codec);
        if (hr < 0) return hr;
        try
        {
            var v = new Variant { Vt = isBool ? VT_BOOL : VT_UI4, UInt = isBool ? (value != 0 ? 0xFFFFu : 0u) : value };
            IntPtr* vtbl = *(IntPtr**)codec;
            var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)vtbl[9];
            return setValue(codec, &api, &v);
        }
        finally { Marshal.Release(codec); }
    }
}
