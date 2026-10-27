using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Platform.Apple;

namespace Plural.Platform.MacOS;

/// <summary>System colors from AppKit, resolved for light (Aqua) and dark (DarkAqua) mode. Call on the main thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacAppearance
{
    static MacAppearance() => NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

    /// <summary>NSColor.windowBackgroundColor (what the title bar blends with) as 0xRRGGBB: (light, dark).</summary>
    public static (uint Light, uint Dark) WindowBackground() =>
        (Resolve("NSAppearanceNameAqua", "windowBackgroundColor"), Resolve("NSAppearanceNameDarkAqua", "windowBackgroundColor"));

    private static uint Resolve(string appearanceName, string colorName)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        var cls = ObjC.Class("NSAppearance");
        var selCurrent = ObjC.Sel("currentAppearance");
        var selSetCurrent = ObjC.Sel("setCurrentAppearance:");
        var previous = ObjC.Send(cls, selCurrent);
        try
        {
            // Dynamic NSColors resolve against the current appearance; setCurrentAppearance: avoids an Objective-C block.
            ObjC.Send(cls, selSetCurrent, ObjC.Send(cls, ObjC.Sel("appearanceNamed:"), Str(appearanceName)));
            var color = ObjC.Send(ObjC.Send(ObjC.Class("NSColor"), ObjC.Sel(colorName)), ObjC.Sel("colorUsingColorSpace:"),
                ObjC.Send(ObjC.Class("NSColorSpace"), ObjC.Sel("sRGBColorSpace")));
            if (color == IntPtr.Zero)
            {
                throw new InvalidOperationException($"{colorName} has no sRGB representation");
            }
            static uint Byte(double c) => (uint)Math.Clamp(Math.Round(c * 255), 0, 255);
            return Byte(ObjC.SendDouble(color, ObjC.Sel("redComponent"))) << 16
                 | Byte(ObjC.SendDouble(color, ObjC.Sel("greenComponent"))) << 8
                 | Byte(ObjC.SendDouble(color, ObjC.Sel("blueComponent")));
        }
        finally
        {
            ObjC.Send(cls, selSetCurrent, previous);
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private static IntPtr Str(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes)
        {
            return ObjC.Send(ObjC.Class("NSString"), ObjC.Sel("stringWithUTF8String:"), (IntPtr)p);
        }
    }
}
