using System.Runtime.InteropServices;

namespace Plural.Platform.Apple;

[StructLayout(LayoutKind.Sequential)]
internal struct CFRange(nint location, nint length)
{
    public nint Location = location;
    public nint Length = length;
}

/// <summary>The few CoreFoundation calls we need (strings, reference counting, run loops).</summary>
internal static unsafe partial class CF
{
    public const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(Lib)] public static partial void CFRelease(IntPtr cf);
    [LibraryImport(Lib)] public static partial IntPtr CFRetain(IntPtr cf);
    [LibraryImport(Lib)] public static partial nint CFStringGetLength(IntPtr s);
    [LibraryImport(Lib)] public static partial void CFStringGetCharacters(IntPtr s, CFRange range, char* buffer);
    [LibraryImport(Lib)] public static partial IntPtr CFStringCreateWithCharacters(IntPtr alloc, char* chars, nint length);

    [LibraryImport(Lib)] public static partial IntPtr CFRunLoopGetCurrent();
    [LibraryImport(Lib)] public static partial void CFRunLoopRun();
    [LibraryImport(Lib)] public static partial void CFRunLoopWakeUp(IntPtr runLoop);
    [LibraryImport(Lib)] public static partial IntPtr CFRunLoopSourceCreate(IntPtr alloc, nint order, CFRunLoopSourceContext* context);
    [LibraryImport(Lib)] public static partial void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
    [LibraryImport(Lib)] public static partial void CFRunLoopSourceSignal(IntPtr source);

    private static readonly Lazy<IntPtr> DefaultMode = new(() =>
        *(IntPtr*)NativeLibrary.GetExport(NativeLibrary.Load(Lib), "kCFRunLoopDefaultMode"));

    /// <summary>kCFRunLoopDefaultMode (an exported CFStringRef variable).</summary>
    public static IntPtr RunLoopDefaultMode => DefaultMode.Value;

    /// <summary>Copies a CFStringRef (or toll-free bridged NSString) to a managed string; null for NULL.</summary>
    public static string? ToManaged(IntPtr s)
    {
        if (s == IntPtr.Zero)
        {
            return null;
        }
        var length = CFStringGetLength(s);
        if (length == 0)
        {
            return "";
        }
        var chars = new char[length];
        fixed (char* p = chars)
        {
            CFStringGetCharacters(s, new CFRange(0, length), p);
        }
        return new string(chars);
    }

    /// <summary>Copies a CFStringRef we own (from a Copy/Create call or a Core Audio property) and releases it.</summary>
    public static string? ToManagedAndRelease(IntPtr s)
    {
        try
        {
            return ToManaged(s);
        }
        finally
        {
            if (s != IntPtr.Zero)
            {
                CFRelease(s);
            }
        }
    }

    /// <summary>New CFStringRef (+1 reference; release with <see cref="CFRelease"/>).</summary>
    public static IntPtr CreateString(string s)
    {
        fixed (char* p = s)
        {
            return CFStringCreateWithCharacters(IntPtr.Zero, p, s.Length);
        }
    }
}

/// <summary>CFRunLoopSourceContext (version 0).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CFRunLoopSourceContext
{
    public nint Version;
    public IntPtr Info;
    public IntPtr Retain;
    public IntPtr Release;
    public IntPtr CopyDescription;
    public IntPtr Equal;
    public IntPtr Hash;
    public IntPtr Schedule;
    public IntPtr Cancel;
    public delegate* unmanaged<IntPtr, void> Perform;
}

/// <summary>Four-character codes used throughout Core Audio, and OSStatus formatting.</summary>
internal static class FourCC
{
    public static uint Of(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);

    /// <summary>"'abcd'" when the status is a printable four-character code, else the number.</summary>
    public static string Describe(int status)
    {
        Span<char> c = [(char)((status >> 24) & 0xFF), (char)((status >> 16) & 0xFF), (char)((status >> 8) & 0xFF), (char)(status & 0xFF)];
        foreach (var ch in c)
        {
            if (ch < 0x20 || ch > 0x7E)
            {
                return status.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return $"'{new string(c)}'";
    }
}
