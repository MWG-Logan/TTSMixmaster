using System.Net;
using System.Text;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

[TestClass]
public sealed class SpotifyServiceTests
{
    internal static AppSettings Settings(bool oauth = false) => new() { SpotifyClientId = "fake-client", SpotifyClientSecret = "fake-secret", SpotifyUserId = "listener", SpotifyUseOauth = oauth };
    internal static void Token(FakeHttp fake, string access = "fake-access") => fake.Add(new { access_token = access, expires_in = 3600, token_type = "Bearer" });
    private const string Metadata = """{"id":"playlist123","name":"Playlist","description":"A &amp; B","owner":{"display_name":"Owner"},"tracks":{"total":3},"images":[{"url":"https://image.test/a"}]}""";
    private const string Track = """{"type":"track","id":"track1","name":"Song","artists":[{"name":"A"},{"name":"B"}],"album":{"name":"Album","images":[{"url":"https://image.test/track"}]},"duration_ms":182500}""";

    [TestMethod]
    public async Task PublicListingPaginatesAndUsesClientCredentials()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(async request =>
        {
            Assert.AreEqual("https://accounts.spotify.com/api/token", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Basic", request.Headers.Authorization!.Scheme);
            Assert.AreEqual("fake-client:fake-secret", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
            Assert.AreEqual("client_credentials", FakeHttp.Form(await request.Content!.ReadAsStringAsync())["grant_type"]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"fake-access","expires_in":3600}""") };
        });
        fake.Add("{\"items\":[" + Metadata + "],\"next\":\"https://api.spotify.com/v1/users/listener/playlists?offset=50\"}", r =>
        { Assert.EndsWith("/users/listener/playlists", r.RequestUri!.AbsolutePath); Assert.AreEqual("Bearer", r.Headers.Authorization!.Scheme); });
        fake.Add("""{"items":[{"id":"second","name":"Second"}],"next":null}""", r => Assert.AreEqual("50", FakeHttp.Query(r.RequestUri!)["offset"]));
        var result = await new SpotifyService(Settings(), http, new(root.Root)).GetMyCollectionsAsync();
        Assert.AreEqual(2, result.Count); foreach (var x in result) Assert.AreEqual(CollectionKind.Playlist, x.Kind);
        Assert.AreEqual("Owner", result[0].Owner); Assert.AreEqual("A & B", result[0].Description); Assert.AreEqual(3, result[0].TrackCount);
        Assert.IsFalse(File.Exists(Path.Combine(root.Root, "spotify_token.json")));
    }

    [TestMethod]
    public async Task OAuthListingMapsLikedAsPlaylistAndRecentAndTopAsCharts()
    {
        using var root = new TemporaryData(); var paths = new AppPaths(root.Root);
        await new SpotifyTokenStore(paths).SaveAsync("fake-client", "fake-refresh");
        using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("{\"items\":[" + Metadata + "],\"next\":null}", r => Assert.EndsWith("/me/playlists", r.RequestUri!.AbsolutePath));
        fake.Add("""{"items":[],"next":null}""", r => Assert.EndsWith("/me/albums", r.RequestUri!.AbsolutePath));
        var result = await new SpotifyService(Settings(true), http, paths).GetMyCollectionsAsync();
        Assert.AreEqual(6, result.Count);
        Assert.AreEqual(CollectionKind.Playlist, result.Single(x => x.Id == "spotify:liked").Kind);
        foreach (var x in result.Where(x => x.Id == "spotify:recent" || x.Id.StartsWith("spotify:top:"))) Assert.AreEqual(CollectionKind.Chart, x.Kind);
    }

    [TestMethod]
    [DataRow("playlist123")]
    [DataRow("spotify:playlist:playlist123")]
    [DataRow("https://open.spotify.com/playlist/playlist123?si=share")]
    [DataRow("https://open.spotify.com/intl-en/playlist/playlist123?si=share")]
    public async Task PlaylistIdAndShareUrlsLoadAllTracks(string id)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add(Metadata, r => Assert.EndsWith("/playlists/playlist123", r.RequestUri!.AbsolutePath));
        fake.Add("{\"items\":[{\"item\":" + Track + "},{\"item\":null},{\"item\":{\"type\":\"episode\"}}],\"next\":\"https://api.spotify.com/v1/playlists/playlist123/items?offset=50\"}", r => Assert.EndsWith("/items", r.RequestUri!.AbsolutePath));
        fake.Add("{\"items\":[{\"track\":" + Track + "}],\"next\":null}", r => Assert.AreEqual("50", FakeHttp.Query(r.RequestUri!)["offset"]));
        var result = await new SpotifyService(Settings(), http, new(root.Root)).GetCollectionAsync(id);
        Assert.AreEqual(2, result.Tracks.Count);
        foreach (var x in result.Tracks) { Assert.AreEqual("A, B", x.Artist); Assert.AreEqual(TimeSpan.FromMilliseconds(182500), x.Duration); Assert.AreEqual("Album", x.Album); Assert.IsEmpty(x.SourceUrl); }
        Assert.AreEqual(4, result.Tracks[1].TrackNumber);
        Assert.AreEqual(4, fake.Requests);
    }

    [TestMethod]
    [DataRow("spotify:liked", "/v1/me/tracks", false)]
    [DataRow("spotify:recent", "/v1/me/player/recently-played", false)]
    [DataRow("spotify:top:short_term", "/v1/me/top/tracks", true)]
    [DataRow("spotify:top:medium_term", "/v1/me/top/tracks", true)]
    [DataRow("spotify:top:long_term", "/v1/me/top/tracks", true)]
    public async Task PersonalCollectionsPageAndRetainTheirKind(string id, string endpoint, bool top)
    {
        using var root = new TemporaryData(); var paths = new AppPaths(root.Root); await new SpotifyTokenStore(paths).SaveAsync("fake-client", "fake-refresh");
        using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        var entry = top ? Track : "{\"track\":" + Track + "}";
        fake.Add("{\"items\":[" + entry + "],\"next\":\"https://api.spotify.com" + endpoint + "?before=123\"}", r =>
        {
            Assert.AreEqual(endpoint, r.RequestUri!.AbsolutePath);
            if (top) Assert.AreEqual(id[12..], FakeHttp.Query(r.RequestUri!)["time_range"]);
        });
        fake.Add("{\"items\":[" + entry + "],\"next\":null}");
        var result = await new SpotifyService(Settings(true), http, paths).GetCollectionAsync(id);
        Assert.AreEqual(2, result.Tracks.Count);
        Assert.AreEqual(id == "spotify:liked" ? CollectionKind.Playlist : CollectionKind.Chart, result.Summary.Kind);
    }

    [TestMethod]
    public async Task PlaylistSearchPagesSkipsNullResultsAndDoesNotSearchCharts()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("{\"playlists\":{\"items\":[null," + Metadata + "],\"next\":\"https://api.spotify.com/v1/search?offset=10&type=playlist&q=test\"}}", r => Assert.AreEqual("playlist", FakeHttp.Query(r.RequestUri!)["type"]));
        fake.Add("""{"playlists":{"items":[{"id":"two","name":"Two"}],"next":null}}""");
        var service = new SpotifyService(Settings(), http, new(root.Root));
        Assert.AreEqual(2, (await service.SearchAsync("test", CollectionKind.Playlist, 2)).Count);
        Assert.IsEmpty(await service.SearchAsync("test", CollectionKind.Chart));
        Assert.AreEqual(3, fake.Requests);
    }

    [TestMethod]
    [DataRow("https://open.spotify.com/track/track123")]
    [DataRow("https://open.spotify.com/artist/artist123")]
    [DataRow("spotify:album:")]
    [DataRow("https://evil.test/album/album123")]
    [DataRow("https://evil.test/playlist/playlist123")]
    [DataRow("spotify:top:invalid")]
    public async Task InvalidUrlsFailBeforeAuthentication(string id)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        await Assert.ThrowsExactlyAsync<ServiceException>(() => new SpotifyService(Settings(), http, new(root.Root)).GetCollectionAsync(id));
        Assert.AreEqual(0, fake.Requests);
    }

    [TestMethod]
    public async Task RejectsForeignPaginationBeforeLeakingBearerToken()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("""{"items":[],"next":"https://evil.test/v1/steal"}""");
        await Assert.ThrowsExactlyAsync<ServiceException>(() => new SpotifyService(Settings(), http, new(root.Root)).GetMyCollectionsAsync());
        Assert.AreEqual(2, fake.Requests);
    }

    [TestMethod]
    public async Task UnauthorizedRetriesOnceWithFreshToken()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake, "first");
        fake.Add("secret", status: HttpStatusCode.Unauthorized); Token(fake, "second");
        fake.Add("""{"items":[],"next":null}""", r => Assert.AreEqual("second", r.Headers.Authorization!.Parameter));
        Assert.IsEmpty(await new SpotifyService(Settings(), http, new(root.Root)).GetMyCollectionsAsync());
        Assert.AreEqual(4, fake.Requests);
    }

    [TestMethod]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(429)]
    public async Task ErrorsThrowAndConnectionReturnsMessageWithoutSecrets(int status)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("fake-secret", status: (HttpStatusCode)status); fake.Add("fake-secret", status: (HttpStatusCode)status);
        var service = new SpotifyService(Settings(), http, new(root.Root));
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => service.GetCollectionAsync("playlist123"));
        Assert.AreEqual((HttpStatusCode)status, error.StatusCode); Assert.DoesNotContain("fake-secret", error.ToString());
        Assert.IsNotNull(await service.TestConnectionAsync());
    }

    [TestMethod]
    public async Task MissingCredentialsReturnConnectionMessageAndPublicModeRejectsPersonalCharts()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var service = new SpotifyService(new AppSettings(), http, new(root.Root));
        Assert.IsFalse(service.IsConfigured); Assert.IsNotNull(await service.TestConnectionAsync());
        await Assert.ThrowsExactlyAsync<ServiceException>(() => new SpotifyService(Settings(), http, new(root.Root)).GetCollectionAsync("spotify:liked"));
        Assert.AreEqual(0, fake.Requests);
    }
}
