using Plural.Core;

namespace Plural.Core.Tests;

public class FileNamesTests
{
    [Theory]
    [InlineData("Bluetooth: SmartMike+", "Bluetooth_ SmartMike+")]
    [InlineData("a<b>c\"d/e\\f|g?h*i", "a_b_c_d_e_f_g_h_i")]
    [InlineData("Mic…1", "Mic_1")]
    [InlineData("  name.  ", "name")]
    [InlineData("trail...", "trail")]
    [InlineData("Mikrofon Ärger 麦克风", "Mikrofon Ärger 麦克风")]
    [InlineData("a\tb\u0001c", "a_b_c")]
    public void Replaces_invalid_characters(string input, string expected) => Assert.Equal(expected, FileNames.Sanitize(input, "x"));

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("con.wav", "_con.wav")]
    [InlineData("LPT1.txt", "_LPT1.txt")]
    [InlineData("COM9", "_COM9")]
    [InlineData("COM10", "COM10")]
    [InlineData("Console", "Console")]
    public void Avoids_reserved_device_names(string input, string expected) => Assert.Equal(expected, FileNames.Sanitize(input, "x"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void Falls_back_when_empty(string? input) => Assert.Equal("fb", FileNames.Sanitize(input, "fb"));

    [Fact]
    public void Long_names_are_capped_and_keep_the_extension()
    {
        var s = FileNames.Sanitize(new string('a', 300) + ".wav", "x");
        Assert.Equal(FileNames.MaxLength, s.Length);
        Assert.EndsWith(".wav", s);
        Assert.Equal(FileNames.MaxLength, FileNames.Sanitize(new string('b', 300), "x").Length);
    }

    [Fact]
    public void Result_is_valid_on_windows()
    {
        var s = FileNames.Sanitize("Bluetooth: SmartMike+.wav", "x");
        Assert.DoesNotContain(':', s);
        Assert.Equal(s, FileNames.Sanitize(s, "x")); // idempotent
    }
}
