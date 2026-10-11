using System.Reflection;
using System.Runtime.InteropServices;

namespace Plural.Core.Codec;

/// <summary>
/// Single DllImport resolver for Core's native libraries (only one resolver can be registered per assembly).
/// Desktop: <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c> next to the app; Android: loaded by soname from the APK.
/// </summary>
internal static class NativeLibs
{
    private static readonly object Gate = new();
    private static bool _registered;

    // import name -> (windows file, unix soname, macOS dylib)
    private static readonly Dictionary<string, (string Win, string Unix, string Mac)> Libs = new()
    {
        ["celt071"] = ("celt071.dll", "libcelt071.so", "libcelt071.dylib"),
        ["mp3lame"] = ("mp3lame.dll", "libmp3lame.so", "libmp3lame.dylib"),
    };

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }
            NativeLibrary.SetDllImportResolver(typeof(NativeLibs).Assembly, Resolve);
            _registered = true;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (!Libs.TryGetValue(name, out var f))
        {
            return IntPtr.Zero;
        }
        if (OperatingSystem.IsAndroid())
        {
            return NativeLibrary.TryLoad(f.Unix, out var lib) ? lib : IntPtr.Zero;
        }
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            var a => a.ToString().ToLowerInvariant(),
        };
        var file = OperatingSystem.IsWindows() ? f.Win : OperatingSystem.IsMacOS() ? f.Mac : f.Unix;
        var candidate = Path.Combine(AppContext.BaseDirectory, "runtimes", $"{os}-{arch}", "native", file);
        if (NativeLibrary.TryLoad(candidate, out var handle))
        {
            return handle;
        }
        return NativeLibrary.TryLoad(name, assembly, path, out handle) ? handle : IntPtr.Zero;
    }
}
