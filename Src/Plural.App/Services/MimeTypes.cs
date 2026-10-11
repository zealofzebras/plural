using System.IO;

namespace Plural.App.Services;

public static class MimeTypes
{
    public static string For(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".mka" => "audio/x-matroska",
        ".mkv" => "video/x-matroska",
        ".mp4" => "video/mp4",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
