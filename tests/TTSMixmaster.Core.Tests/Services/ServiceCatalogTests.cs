using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

[TestClass]
public sealed class ServiceCatalogTests
{
    [TestMethod]
    public void ProvidesAllServicesWithoutNetworkOrBrowserAndReflectsSettingsChanges()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = new AppSettings();
        var catalog = new ServiceCatalog(() => settings, http, new(root.Root), _ => throw new Exception("Must not launch"));
        Assert.AreEqual(3, catalog.All.Count());
        foreach (var x in catalog.All) Assert.IsFalse(x.IsConfigured);
        settings = new AppSettings { LastfmApiKey = "key", LastfmUsername = "listener", YoutubeApiKey = "key", SpotifyClientId = "client" };
        foreach (var x in catalog.All) Assert.IsTrue(x.IsConfigured);
        Assert.IsExactInstanceOfType<SpotifyService>(catalog[ServiceKind.Spotify]);
        Assert.AreEqual(0, fake.Requests);
    }

    [TestMethod]
    public async Task NetworkAndMalformedJsonErrorsBecomeServiceExceptions()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(_ => throw new HttpRequestException("secret-url?key=secret"));
        fake.Add("not-json-secret");
        var service = new YouTubeService(new AppSettings { YoutubeApiKey = "key" }, http);
        Assert.DoesNotContain("secret", await service.TestConnectionAsync() ?? "");
        Assert.DoesNotContain("secret", await service.TestConnectionAsync() ?? "");
    }
}
