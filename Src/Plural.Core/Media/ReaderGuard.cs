namespace Plural.Core.Media;

/// <summary>
/// Demuxers promise to throw only <see cref="InvalidDataException"/> (corrupt content) or <see cref="IOException"/> for bad files; parser slips
/// on hostile bytes (index, argument, overflow...) are converted with this helper.
/// </summary>
internal static class ReaderGuard
{
    /// <summary>True for exceptions that must be converted (everything except the documented ones, out of memory and use-after-dispose).</summary>
    public static bool IsForeign(Exception ex) => ex is not (InvalidDataException or IOException or OutOfMemoryException or ObjectDisposedException);

    public static InvalidDataException Wrap(string what, Exception ex) => new($"Corrupt {what} file: {ex.Message}", ex);
}
