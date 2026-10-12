namespace Plural.Core;

/// <summary>
/// Portable file and folder names: the result is valid on Windows, macOS, Linux, Android, iOS and inside zip files, whatever OS
/// produced the text (Android and Linux allow characters such as ':' that NTFS treats as an alternate-stream separator).
/// </summary>
public static class FileNames
{
    public const int MaxLength = 120;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A name (file or folder, no directory part) that is safe everywhere: control characters, <c>&lt;&gt;:"/\|?*</c> and '…' become '_',
    /// surrounding whitespace and trailing dots/spaces are removed, Windows device names (also with an extension) get a '_' prefix,
    /// the length is capped at <see cref="MaxLength"/> keeping the extension, and <paramref name="fallback"/> is returned when nothing is left.
    /// Non-ASCII letters are kept.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "file")
    {
        if (string.IsNullOrWhiteSpace(name)) { return fallback; }
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or '…')
            {
                chars[i] = '_';
            }
        }
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        if (s.Length == 0)
        {
            return fallback;
        }

        if (s.Length > MaxLength)
        {
            var dot = s.LastIndexOf('.');
            var ext = dot > 0 && s.Length - dot <= 10 ? s[dot..] : "";
            s = s[..(MaxLength - ext.Length)].TrimEnd('.', ' ') + ext;
        }

        var firstDot = s.IndexOf('.');
        var stem = firstDot < 0 ? s : s[..firstDot];
        if (Reserved.Contains(stem.TrimEnd(' ')))
        {
            s = "_" + s;
        }
        return s;
    }
}
