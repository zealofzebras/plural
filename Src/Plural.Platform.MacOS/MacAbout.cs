using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plural.Platform.Apple;

namespace Plural.Platform.MacOS;

/// <summary>The standard macOS About panel (NSApplication orderFrontStandardAboutPanelWithOptions:). Call on the main thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacAbout
{
    /// <summary>Shows the panel; the website is the first line of the credits, as a link.</summary>
    public static void Show(string name, string version, string websiteUrl, string details, string copyright)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            var website = new Uri(websiteUrl).Host;

            var attrs = NewDictionary();
            Set(attrs, "NSFont", ObjC.Send(ObjC.Class("NSFont"), ObjC.Sel("systemFontOfSize:"), 11.0));
            // Credits are drawn black unless a color is given; secondaryLabelColor follows light/dark mode.
            Set(attrs, "NSColor", ObjC.Send(ObjC.Class("NSColor"), ObjC.Sel("secondaryLabelColor")));
            var paragraph = ObjC.Send(ObjC.Class("NSMutableParagraphStyle"), ObjC.Sel("new"));
            // NSTextAlignmentCenter is 1 on Apple silicon (iOS values) and 2 on Intel.
            ObjC.Send(paragraph, ObjC.Sel("setAlignment:"), (nuint)(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 2 : 1));
            Set(attrs, "NSParagraphStyle", paragraph);
            ObjC.Release(paragraph);

            var credits = ObjC.Send(ObjC.Send(ObjC.Class("NSMutableAttributedString"), ObjC.Sel("alloc")),
                ObjC.Sel("initWithString:attributes:"), Str(website + "\n\n" + details), attrs);
            ObjC.Send(credits, ObjC.Sel("addAttribute:value:range:"), Str("NSLink"),
                ObjC.Send(ObjC.Class("NSURL"), ObjC.Sel("URLWithString:"), Str(websiteUrl)),
                new ObjC.NSRange(0, (nuint)website.Length));

            var options = NewDictionary();
            Set(options, "ApplicationName", Str(name));
            Set(options, "ApplicationVersion", Str(version));
            Set(options, "Credits", credits);
            Set(options, "Copyright", Str(copyright));
            ObjC.Release(credits);

            var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
            ObjC.Send(app, ObjC.Sel("activateIgnoringOtherApps:"), (nuint)1);
            ObjC.Send(app, ObjC.Sel("orderFrontStandardAboutPanelWithOptions:"), options);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private static IntPtr NewDictionary() => ObjC.Send(ObjC.Class("NSMutableDictionary"), ObjC.Sel("dictionary"));

    private static void Set(IntPtr dict, string key, IntPtr value) =>
        ObjC.Send(dict, ObjC.Sel("setObject:forKey:"), value, Str(key));

    private static IntPtr Str(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes)
        {
            return ObjC.Send(ObjC.Class("NSString"), ObjC.Sel("stringWithUTF8String:"), (IntPtr)p);
        }
    }
}
