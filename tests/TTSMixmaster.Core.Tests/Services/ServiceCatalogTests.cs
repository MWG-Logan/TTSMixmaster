using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

public sealed class ServiceCatalogTests
{
    [Fact]
    public void ProvidesAllServicesWithoutNetworkOrBrowserAndReflectsSettingsChanges()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = new AppSettings();
        var catalog = new ServiceCatalog(() => settings, http, new(root.Root), _ => throw new Exception("Must not launch"));
        Assert.Equal(3, catalog.All.Count());
        Assert.All(catalog.All, x => Assert.False(x.IsConfigured));
        settings = new AppSettings { LastfmApiKey = "key", LastfmUsername = "listener", YoutubeApiKey = "key", SpotifyClientId = "client" };
        Assert.All(catalog.All, x => Assert.True(x.IsConfigured));
        Assert.IsType<SpotifyService>(catalog[ServiceKind.Spotify]);
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task NetworkAndMalformedJsonErrorsBecomeServiceExceptions()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(_ => throw new HttpRequestException("secret-url?key=secret"));
        fake.Add("not-json-secret");
        var service = new YouTubeService(new AppSettings { YoutubeApiKey = "key" }, http);
        Assert.DoesNotContain("secret", await service.TestConnectionAsync());
        Assert.DoesNotContain("secret", await service.TestConnectionAsync());
    }
}
