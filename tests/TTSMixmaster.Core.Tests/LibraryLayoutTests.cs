using TTSMixmaster.Core.Files;

namespace TTSMixmaster.Core.Tests;

public sealed class LibraryLayoutTests
{
    [Theory]
    [InlineData("AC/DC - Back In Black", "AC DC - Back In Black")]
    [InlineData("  What?   Is: This  ", "What Is This")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("???", "Untitled")]
    public void SanitizeFileName_produces_safe_segments(string input, string expected) =>
        Assert.Equal(expected, LibraryLayout.SanitizeFileName(input));

    [Fact]
    public void SanitizeFileName_caps_length() =>
        Assert.Equal(150, LibraryLayout.SanitizeFileName(new string('a', 400)).Length);
}
