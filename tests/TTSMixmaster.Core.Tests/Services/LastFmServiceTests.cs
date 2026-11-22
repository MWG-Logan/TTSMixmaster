using System.Net;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

[TestClass]
public sealed class LastFmServiceTests
{
    private static AppSettings Settings() => new() { LastfmApiKey = "fake-key", LastfmUsername = "listener" };

    [TestMethod]
    public async Task ListsChartsThenTopAlbumsAndDoesNotSearchPlaylists()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"topalbums":{"album":[],"@attr":{"totalPages":"0"}}}""", r => Assert.AreEqual("user.gettopalbums", FakeHttp.Query(r.RequestUri!)["method"]));
        var service = new LastFmService(Settings(), http);
        var collections = await service.GetMyCollectionsAsync();
        Assert.AreEqual(3, collections.Count);
        foreach (var x in collections) { Assert.AreEqual(CollectionKind.Chart, x.Kind); Assert.AreEqual(ServiceKind.LastFm, x.Service); Assert.Contains("user=listener", x.Id); }
        Assert.IsEmpty(await service.SearchAsync("music", CollectionKind.Playlist));
        Assert.IsEmpty(await service.SearchAsync("music", CollectionKind.Chart));
        Assert.AreEqual(1, fake.Requests);
    }

    [TestMethod]
    [DataRow("overall")]
    [DataRow("7day")]
    [DataRow("1month")]
    [DataRow("3month")]
    [DataRow("6month")]
    [DataRow("12month")]
    public async Task TopChartPagesToRequestedLimitAndPreservesOptions(string period)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"toptracks":{"track":[{"name":"First","artist":{"name":"Artist"},"mbid":"mbid1","playcount":"42"}],"@attr":{"totalPages":"3"}}}""", r =>
        {
            var q = FakeHttp.Query(r.RequestUri!); Assert.AreEqual(period, q["period"]); Assert.AreEqual("1", q["page"]); Assert.AreEqual("2", q["limit"]); Assert.AreEqual("https", r.RequestUri!.Scheme);
        });
        fake.Add("""{"toptracks":{"track":[{"name":"Second","artist":"Artist"},{"name":"Ignored","artist":"Artist"}],"@attr":{"totalPages":"3"}}}""", r => Assert.AreEqual("2", FakeHttp.Query(r.RequestUri!)["page"]));
        var result = await new LastFmService(Settings(), http).GetCollectionAsync(LastFmService.CreateCollectionId("top_tracks", period, 2));
        Assert.AreSequenceEqual(new[] { "First", "Second" }, result.Tracks.Select(x => x.Title));
        Assert.AreEqual(42, result.Tracks[0].PlayCount);
        Assert.AreEqual("Artist", result.Tracks[0].Artist);
        Assert.AreEqual("mbid1", result.Tracks[0].ServiceId);
        Assert.IsEmpty(result.Tracks[0].SourceUrl);
        Assert.AreEqual(2, result.Summary.TrackCount);
        Assert.AreEqual(0, fake.Remaining);
    }

    [TestMethod]
    [DataRow("loved_tracks", "lovedtracks")]
    [DataRow("recent_tracks", "recenttracks")]
    public async Task LovedAndRecentPageAndSkipNowPlaying(string id, string root)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("{\"" + root + "\":{\"track\":[{\"name\":\"Live\",\"@attr\":{\"nowplaying\":\"true\"}},{\"name\":\"First\",\"artist\":{\"#text\":\"Artist\"},\"album\":{\"#text\":\"Album\"}}],\"@attr\":{\"totalPages\":\"2\"}}}");
        fake.Add("{\"" + root + "\":{\"track\":{\"name\":\"Second\",\"artist\":\"Artist\"},\"@attr\":{\"totalPages\":\"2\"}}}");
        var result = await new LastFmService(Settings(), http).GetCollectionAsync(LastFmService.CreateCollectionId(id, limit: 2));
        Assert.AreEqual(2, result.Tracks.Count);
        Assert.AreEqual("Album", result.Tracks[0].Album);
        foreach (var x in result.Tracks) Assert.AreEqual("Artist", x.Artist);
        Assert.AreEqual(2, fake.Requests);
    }

    [TestMethod]
    [DataRow("https://www.last.fm/user/shared/library/tracks?date_preset=LAST_7_DAYS", "user.gettoptracks", "7day")]
    [DataRow("https://www.last.fm/user/shared/loved", "user.getlovedtracks", "overall")]
    [DataRow("https://last.fm/user/shared/library", "user.getrecenttracks", "overall")]
    public async Task ParsesLastFmUserChartUrls(string url, string method, string period)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"toptracks":{"track":[]},"lovedtracks":{"track":[]},"recenttracks":{"track":[]}}""", r =>
        {
            var q = FakeHttp.Query(r.RequestUri!); Assert.AreEqual("shared", q["user"]); Assert.AreEqual(method, q["method"]); Assert.AreEqual(period, q["period"]);
        });
        var result = await new LastFmService(Settings(), http).GetCollectionAsync(url);
        Assert.AreEqual("shared", result.Summary.Owner);
        Assert.AreEqual(CollectionKind.Chart, result.Summary.Kind);
    }

    [TestMethod]
    [DataRow("https://evil.test/user/shared/loved")]
    [DataRow("top_tracks?period=bad")]
    [DataRow("top_tracks?limit=0")]
    [DataRow("top_tracks?limit=abc")]
    public async Task InvalidIdsFailBeforeNetwork(string id)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        await Assert.ThrowsExactlyAsync<ServiceException>(() => new LastFmService(Settings(), http).GetCollectionAsync(id));
        Assert.AreEqual(0, fake.Requests);
    }

    [TestMethod]
    [DataRow(200, "{\"error\":10,\"message\":\"fake-key\"}", "authentication")]
    [DataRow(429, "fake-key", "limit")]
    [DataRow(404, "fake-key", "not found")]
    public async Task ErrorsAreSafeAndConnectionReturnsMessage(int status, string body, string expected)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(body, status: (HttpStatusCode)status); fake.Add(body, status: (HttpStatusCode)status);
        var service = new LastFmService(Settings(), http);
        var ex = await Assert.ThrowsExactlyAsync<ServiceException>(() => service.GetCollectionAsync("top_tracks"));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("fake-key", ex.ToString());
        Assert.IsNotNull(await service.TestConnectionAsync());
    }

    [TestMethod]
    public async Task SettingsCanChangeAndCancellationIsPreserved()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        var settings = new AppSettings(); var service = new LastFmService(() => settings, http);
        Assert.IsFalse(service.IsConfigured); Assert.IsNotNull(await service.TestConnectionAsync());
        settings = Settings(); Assert.IsTrue(service.IsConfigured);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetCollectionAsync("top_tracks", canceled.Token));
    }
}
