using System.Text.Json;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

[TestClass]
public sealed class LastFmArtTests
{
    [TestMethod]
    [DataRow("""{"image":[{"#text":"https://img.test/s.png"},{"#text":"https://img.test/xl.png"},{"#text":""}]}""", "https://img.test/xl.png")]
    [DataRow("""{"image":[{"#text":"https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png"}]}""", "")]
    [DataRow("""{"image":[]}""", "")]
    [DataRow("""{}""", "")]
    public void The_largest_image_is_used_and_the_grey_star_placeholder_is_no_image(string json, string expected)
    {
        using var item = JsonDocument.Parse(json);
        Assert.AreEqual(expected, LastFmService.LargestImage(item.RootElement));
    }
}
