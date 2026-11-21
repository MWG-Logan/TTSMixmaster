using System.Net;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

public sealed class LastFmServiceTests
{
    private static AppSettings Settings() => new() { LastfmApiKey = "fake-key", LastfmUsername = "listener" };

    [Fact]
    public async Task ListsChartsAndDoesNotSearchAlbumsOrPlaylists()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        var service = new LastFmService(Settings(), http);
        var collections = await service.GetMyCollectionsAsync();
        Assert.Equal(3, collections.Count);
        Assert.All(collections, x => { Assert.Equal(CollectionKind.Chart, x.Kind); Assert.Equal(ServiceKind.LastFm, x.Service); Assert.Contains("user=listener", x.Id); });
        Assert.Empty(await service.SearchAsync("music", CollectionKind.Playlist));
        Assert.Empty(await service.SearchAsync("music", CollectionKind.Album));
        Assert.Equal(0, fake.Requests);
    }

    [Theory]
    [InlineData("overall")]
    [InlineData("7day")]
    [InlineData("1month")]
    [InlineData("3month")]
    [InlineData("6month")]
    [InlineData("12month")]
    public async Task TopChartPagesToRequestedLimitAndPreservesOptions(string period)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"toptracks":{"track":[{"name":"First","artist":{"name":"Artist"},"mbid":"mbid1","playcount":"42"}],"@attr":{"totalPages":"3"}}}""", r =>
        {
            var q = FakeHttp.Query(r.RequestUri!); Assert.Equal(period, q["period"]); Assert.Equal("1", q["page"]); Assert.Equal("2", q["limit"]); Assert.Equal("https", r.RequestUri!.Scheme);
        });
        fake.Add("""{"toptracks":{"track":[{"name":"Second","artist":"Artist"},{"name":"Ignored","artist":"Artist"}],"@attr":{"totalPages":"3"}}}""", r => Assert.Equal("2", FakeHttp.Query(r.RequestUri!)["page"]));
        var result = await new LastFmService(Settings(), http).GetCollectionAsync(LastFmService.CreateCollectionId("top_tracks", period, 2));
        Assert.Equal(new[] { "First", "Second" }, result.Tracks.Select(x => x.Title));
        Assert.Equal(42, result.Tracks[0].PlayCount);
        Assert.Equal("Artist", result.Tracks[0].Artist);
        Assert.Equal("mbid1", result.Tracks[0].ServiceId);
        Assert.Empty(result.Tracks[0].SourceUrl);
        Assert.Equal(2, result.Summary.TrackCount);
        Assert.Equal(0, fake.Remaining);
    }

    [Theory]
    [InlineData("loved_tracks", "lovedtracks")]
    [InlineData("recent_tracks", "recenttracks")]
    public async Task LovedAndRecentPageAndSkipNowPlaying(string id, string root)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("{\"" + root + "\":{\"track\":[{\"name\":\"Live\",\"@attr\":{\"nowplaying\":\"true\"}},{\"name\":\"First\",\"artist\":{\"#text\":\"Artist\"},\"album\":{\"#text\":\"Album\"}}],\"@attr\":{\"totalPages\":\"2\"}}}");
        fake.Add("{\"" + root + "\":{\"track\":{\"name\":\"Second\",\"artist\":\"Artist\"},\"@attr\":{\"totalPages\":\"2\"}}}");
        var result = await new LastFmService(Settings(), http).GetCollectionAsync(LastFmService.CreateCollectionId(id, limit: 2));
        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal("Album", result.Tracks[0].Album);
        Assert.All(result.Tracks, x => Assert.Equal("Artist", x.Artist));
        Assert.Equal(2, fake.Requests);
    }

    [Theory]
    [InlineData("https://www.last.fm/user/shared/library/tracks?date_preset=LAST_7_DAYS", "user.gettoptracks", "7day")]
    [InlineData("https://www.last.fm/user/shared/loved", "user.getlovedtracks", "overall")]
    [InlineData("https://last.fm/user/shared/library", "user.getrecenttracks", "overall")]
    public async Task ParsesLastFmUserChartUrls(string url, string method, string period)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"toptracks":{"track":[]},"lovedtracks":{"track":[]},"recenttracks":{"track":[]}}""", r =>
        {
            var q = FakeHttp.Query(r.RequestUri!); Assert.Equal("shared", q["user"]); Assert.Equal(method, q["method"]); Assert.Equal(period, q["period"]);
        });
        var result = await new LastFmService(Settings(), http).GetCollectionAsync(url);
        Assert.Equal("shared", result.Summary.Owner);
        Assert.Equal(CollectionKind.Chart, result.Summary.Kind);
    }

    [Theory]
    [InlineData("https://evil.test/user/shared/loved")]
    [InlineData("top_tracks?period=bad")]
    [InlineData("top_tracks?limit=0")]
    [InlineData("top_tracks?limit=abc")]
    public async Task InvalidIdsFailBeforeNetwork(string id)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        await Assert.ThrowsAsync<ServiceException>(() => new LastFmService(Settings(), http).GetCollectionAsync(id));
        Assert.Equal(0, fake.Requests);
    }

    [Theory]
    [InlineData(200, "{\"error\":10,\"message\":\"fake-key\"}", "authentication")]
    [InlineData(429, "fake-key", "limit")]
    [InlineData(404, "fake-key", "not found")]
    public async Task ErrorsAreSafeAndConnectionReturnsMessage(int status, string body, string expected)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(body, status: (HttpStatusCode)status); fake.Add(body, status: (HttpStatusCode)status);
        var service = new LastFmService(Settings(), http);
        var ex = await Assert.ThrowsAsync<ServiceException>(() => service.GetCollectionAsync("top_tracks"));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("fake-key", ex.ToString());
        Assert.NotNull(await service.TestConnectionAsync());
    }

    [Fact]
    public async Task SettingsCanChangeAndCancellationIsPreserved()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = new AppSettings(); var service = new LastFmService(() => settings, http);
        Assert.False(service.IsConfigured); Assert.NotNull(await service.TestConnectionAsync());
        settings = Settings(); Assert.True(service.IsConfigured);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCollectionAsync("top_tracks", canceled.Token));
    }
}
