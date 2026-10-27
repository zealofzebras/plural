using System.Runtime.InteropServices;

namespace Plural.Platform.Apple;

/// <summary>
/// Minimal Objective-C runtime access. objc_msgSend is called through function pointers typed per call shape, which is what
/// the arm64 and x64 ABIs require (never through a variadic or mismatched signature).
/// </summary>
internal static unsafe partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr objc_getClass(string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr sel_registerName(string name);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [LibraryImport(Lib)] public static partial void objc_registerClassPair(IntPtr cls);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial byte class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [LibraryImport(Lib)] public static partial IntPtr objc_autoreleasePoolPush();
    [LibraryImport(Lib)] public static partial void objc_autoreleasePoolPop(IntPtr pool);

    private static readonly IntPtr MsgSend = NativeLibrary.GetExport(NativeLibrary.Load(Lib), "objc_msgSend");

    // Structs returned in memory (CMTime, 24 bytes) need objc_msgSend_stret on x64; arm64 has no _stret variant.
    private static readonly IntPtr MsgSendStret = RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? NativeLibrary.GetExport(NativeLibrary.Load(Lib), "objc_msgSend_stret")
        : MsgSend;

    public static CMTime SendCMTime(IntPtr obj, IntPtr sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, CMTime>)MsgSendStret)(obj, sel);

    private static readonly IntPtr SelRetain = sel_registerName("retain");
    private static readonly IntPtr SelRelease = sel_registerName("release");
    private static readonly IntPtr SelUtf8 = sel_registerName("UTF8String");

    public static IntPtr Class(string name) => objc_getClass(name);
    public static IntPtr Sel(string name) => sel_registerName(name);

    public static IntPtr Send(IntPtr obj, IntPtr sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)MsgSend)(obj, sel);

    public static IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(obj, sel, a);

    public static IntPtr Send(IntPtr obj, IntPtr sel, nuint a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, nuint, IntPtr>)MsgSend)(obj, sel, a);

    public static IntPtr Send(IntPtr obj, IntPtr sel, ushort a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, ushort, IntPtr>)MsgSend)(obj, sel, a);

    public static IntPtr Send(IntPtr obj, IntPtr sel, double a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, double, IntPtr>)MsgSend)(obj, sel, a);

    public static IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(obj, sel, a, b);

    public static void Send(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b, NSRange c) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, NSRange, void>)MsgSend)(obj, sel, a, b, c);

    public static IntPtr Send(IntPtr obj, IntPtr sel, void* a, uint b) =>
        ((delegate* unmanaged<IntPtr, IntPtr, void*, uint, IntPtr>)MsgSend)(obj, sel, a, b);

    public static IntPtr Send(IntPtr obj, IntPtr sel, byte a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, byte, IntPtr>)MsgSend)(obj, sel, a);

    public static IntPtr Send(IntPtr obj, IntPtr sel, uint a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr>)MsgSend)(obj, sel, a);

    public static IntPtr Send(IntPtr obj, IntPtr sel, CMTime a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, CMTime, IntPtr>)MsgSend)(obj, sel, a);

    public static IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a, IntPtr* b) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr>)MsgSend)(obj, sel, a, b);

    public static IntPtr Send(IntPtr obj, IntPtr sel, IntPtr* a, nuint b) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, nuint, IntPtr>)MsgSend)(obj, sel, a, b);

    public static IntPtr Send(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b, nint c) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, nint, IntPtr>)MsgSend)(obj, sel, a, b, c);

    /// <summary>Methods returning BOOL (signed char on x64, bool on arm64: both one byte).</summary>
    public static bool SendBool(IntPtr obj, IntPtr sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, byte>)MsgSend)(obj, sel) != 0;

    public static bool SendBool(IntPtr obj, IntPtr sel, IntPtr a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)MsgSend)(obj, sel, a) != 0;

    public static bool SendBool(IntPtr obj, IntPtr sel, IntPtr* a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, byte>)MsgSend)(obj, sel, a) != 0;

    public static nint SendNInt(IntPtr obj, IntPtr sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, nint>)MsgSend)(obj, sel);

    public static nint SendNInt(IntPtr obj, IntPtr sel, IntPtr a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint>)MsgSend)(obj, sel, a);

    /// <summary>True when <paramref name="obj"/> implements <paramref name="selector"/> (for APIs newer than the minimum OS).</summary>
    public static bool RespondsTo(IntPtr obj, string selector) =>
        obj != IntPtr.Zero && SendBool(obj, Sel("respondsToSelector:"), Sel(selector));

    /// <summary>[NSError localizedDescription] (null for nil).</summary>
    public static string? ErrorText(IntPtr nsError) =>
        nsError == IntPtr.Zero ? null : ToManaged(Send(nsError, Sel("localizedDescription")));

    public static int SendInt(IntPtr obj, IntPtr sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, int>)MsgSend)(obj, sel);

    public static int SendInt(IntPtr obj, IntPtr sel, IntPtr a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)MsgSend)(obj, sel, a);

    public static int SendInt(IntPtr obj, IntPtr sel, void* a) =>
        ((delegate* unmanaged<IntPtr, IntPtr, void*, int>)MsgSend)(obj, sel, a);

    public static int SendInt(IntPtr obj, IntPtr sel, void* a, ushort b) =>
        ((delegate* unmanaged<IntPtr, IntPtr, void*, ushort, int>)MsgSend)(obj, sel, a, b);

    public static int SendInt(IntPtr obj, IntPtr sel, IntPtr* a, byte b, IntPtr c) =>
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, byte, IntPtr, int>)MsgSend)(obj, sel, a, b, c);

    public static uint SendUInt(IntPtr obj, IntPtr sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, uint>)MsgSend)(obj, sel);

    public static ushort SendUShort(IntPtr obj, IntPtr sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, ushort>)MsgSend)(obj, sel);

    public static nuint SendNUInt(IntPtr obj, IntPtr sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, nuint>)MsgSend)(obj, sel);

    public static double SendDouble(IntPtr obj, IntPtr sel) =>
        ((delegate* unmanaged<IntPtr, IntPtr, double>)MsgSend)(obj, sel);

    public static IntPtr Retain(IntPtr obj) => obj == IntPtr.Zero ? obj : Send(obj, SelRetain);

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero)
        {
            Send(obj, SelRelease);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NSRange(nuint location, nuint length)
    {
        public nuint Location = location;
        public nuint Length = length;
    }

    /// <summary>NSString → string (null for nil).</summary>
    public static string? ToManaged(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(nsString, SelUtf8));
}
