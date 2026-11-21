using System.Net;
using System.Text;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

public sealed class SpotifyServiceTests
{
    internal static AppSettings Settings(bool oauth = false) => new() { SpotifyClientId = "fake-client", SpotifyClientSecret = "fake-secret", SpotifyUserId = "listener", SpotifyUseOauth = oauth };
    internal static void Token(FakeHttp fake, string access = "fake-access") => fake.Add(new { access_token = access, expires_in = 3600, token_type = "Bearer" });
    private const string Metadata = """{"id":"playlist123","name":"Playlist","description":"A &amp; B","owner":{"display_name":"Owner"},"tracks":{"total":3},"images":[{"url":"https://image.test/a"}]}""";
    private const string Track = """{"type":"track","id":"track1","name":"Song","artists":[{"name":"A"},{"name":"B"}],"album":{"name":"Album","images":[{"url":"https://image.test/track"}]},"duration_ms":182500}""";

    [Fact]
    public async Task PublicListingPaginatesAndUsesClientCredentials()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(async request =>
        {
            Assert.Equal("https://accounts.spotify.com/api/token", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            Assert.Equal("fake-client:fake-secret", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
            Assert.Equal("client_credentials", FakeHttp.Form(await request.Content!.ReadAsStringAsync())["grant_type"]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"fake-access","expires_in":3600}""") };
        });
        fake.Add("{\"items\":[" + Metadata + "],\"next\":\"https://api.spotify.com/v1/users/listener/playlists?offset=50\"}", r =>
        { Assert.EndsWith("/users/listener/playlists", r.RequestUri!.AbsolutePath); Assert.Equal("Bearer", r.Headers.Authorization!.Scheme); });
        fake.Add("""{"items":[{"id":"second","name":"Second"}],"next":null}""", r => Assert.Equal("50", FakeHttp.Query(r.RequestUri!)["offset"]));
        var result = await new SpotifyService(Settings(), http, new(root.Root)).GetMyCollectionsAsync();
        Assert.Equal(2, result.Count); Assert.All(result, x => Assert.Equal(CollectionKind.Playlist, x.Kind));
        Assert.Equal("Owner", result[0].Owner); Assert.Equal("A & B", result[0].Description); Assert.Equal(3, result[0].TrackCount);
        Assert.False(File.Exists(Path.Combine(root.Root, "spotify_token.json")));
    }

    [Fact]
    public async Task OAuthListingMapsLikedAsPlaylistAndRecentAndTopAsCharts()
    {
        using var root = new TemporaryData(); var paths = new AppPaths(root.Root);
        await new SpotifyTokenStore(paths).SaveAsync("fake-client", "fake-refresh");
        using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("{\"items\":[" + Metadata + "],\"next\":null}", r => Assert.EndsWith("/me/playlists", r.RequestUri!.AbsolutePath));
        var result = await new SpotifyService(Settings(true), http, paths).GetMyCollectionsAsync();
        Assert.Equal(6, result.Count);
        Assert.Equal(CollectionKind.Playlist, result.Single(x => x.Id == "spotify:liked").Kind);
        Assert.All(result.Where(x => x.Id == "spotify:recent" || x.Id.StartsWith("spotify:top:")), x => Assert.Equal(CollectionKind.Chart, x.Kind));
    }

    [Theory]
    [InlineData("playlist123")]
    [InlineData("spotify:playlist:playlist123")]
    [InlineData("https://open.spotify.com/playlist/playlist123?si=share")]
    [InlineData("https://open.spotify.com/intl-en/playlist/playlist123?si=share")]
    public async Task PlaylistIdAndShareUrlsLoadAllTracks(string id)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add(Metadata, r => Assert.EndsWith("/playlists/playlist123", r.RequestUri!.AbsolutePath));
        fake.Add("{\"items\":[{\"item\":" + Track + "},{\"item\":null},{\"item\":{\"type\":\"episode\"}}],\"next\":\"https://api.spotify.com/v1/playlists/playlist123/items?offset=50\"}", r => Assert.EndsWith("/items", r.RequestUri!.AbsolutePath));
        fake.Add("{\"items\":[{\"track\":" + Track + "}],\"next\":null}", r => Assert.Equal("50", FakeHttp.Query(r.RequestUri!)["offset"]));
        var result = await new SpotifyService(Settings(), http, new(root.Root)).GetCollectionAsync(id);
        Assert.Equal(2, result.Tracks.Count);
        Assert.All(result.Tracks, x => { Assert.Equal("A, B", x.Artist); Assert.Equal(TimeSpan.FromMilliseconds(182500), x.Duration); Assert.Equal("Album", x.Album); Assert.Empty(x.SourceUrl); });
        Assert.Equal(4, result.Tracks[1].TrackNumber);
        Assert.Equal(4, fake.Requests);
    }

    [Theory]
    [InlineData("spotify:liked", "/v1/me/tracks", false)]
    [InlineData("spotify:recent", "/v1/me/player/recently-played", false)]
    [InlineData("spotify:top:short_term", "/v1/me/top/tracks", true)]
    [InlineData("spotify:top:medium_term", "/v1/me/top/tracks", true)]
    [InlineData("spotify:top:long_term", "/v1/me/top/tracks", true)]
    public async Task PersonalCollectionsPageAndRetainTheirKind(string id, string endpoint, bool top)
    {
        using var root = new TemporaryData(); var paths = new AppPaths(root.Root); await new SpotifyTokenStore(paths).SaveAsync("fake-client", "fake-refresh");
        using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        var entry = top ? Track : "{\"track\":" + Track + "}";
        fake.Add("{\"items\":[" + entry + "],\"next\":\"https://api.spotify.com" + endpoint + "?before=123\"}", r =>
        {
            Assert.Equal(endpoint, r.RequestUri!.AbsolutePath);
            if (top) Assert.Equal(id[12..], FakeHttp.Query(r.RequestUri!)["time_range"]);
        });
        fake.Add("{\"items\":[" + entry + "],\"next\":null}");
        var result = await new SpotifyService(Settings(true), http, paths).GetCollectionAsync(id);
        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(id == "spotify:liked" ? CollectionKind.Playlist : CollectionKind.Chart, result.Summary.Kind);
    }

    [Fact]
    public async Task PlaylistSearchPagesSkipsNullResultsAndDoesNotSearchAlbums()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("{\"playlists\":{\"items\":[null," + Metadata + "],\"next\":\"https://api.spotify.com/v1/search?offset=10&type=playlist&q=test\"}}", r => Assert.Equal("playlist", FakeHttp.Query(r.RequestUri!)["type"]));
        fake.Add("""{"playlists":{"items":[{"id":"two","name":"Two"}],"next":null}}""");
        var service = new SpotifyService(Settings(), http, new(root.Root));
        Assert.Equal(2, (await service.SearchAsync("test", CollectionKind.Playlist, 2)).Count);
        Assert.Empty(await service.SearchAsync("test", CollectionKind.Album));
        Assert.Empty(await service.SearchAsync("test", CollectionKind.Chart));
        Assert.Equal(3, fake.Requests);
    }

    [Theory]
    [InlineData("https://open.spotify.com/album/album123")]
    [InlineData("https://evil.test/playlist/playlist123")]
    [InlineData("spotify:top:invalid")]
    public async Task InvalidUrlsFailBeforeAuthentication(string id)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        await Assert.ThrowsAsync<ServiceException>(() => new SpotifyService(Settings(), http, new(root.Root)).GetCollectionAsync(id));
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task RejectsForeignPaginationBeforeLeakingBearerToken()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("""{"items":[],"next":"https://evil.test/v1/steal"}""");
        await Assert.ThrowsAsync<ServiceException>(() => new SpotifyService(Settings(), http, new(root.Root)).GetMyCollectionsAsync());
        Assert.Equal(2, fake.Requests);
    }

    [Fact]
    public async Task UnauthorizedRetriesOnceWithFreshToken()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake, "first");
        fake.Add("secret", status: HttpStatusCode.Unauthorized); Token(fake, "second");
        fake.Add("""{"items":[],"next":null}""", r => Assert.Equal("second", r.Headers.Authorization!.Parameter));
        Assert.Empty(await new SpotifyService(Settings(), http, new(root.Root)).GetMyCollectionsAsync());
        Assert.Equal(4, fake.Requests);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task ErrorsThrowAndConnectionReturnsMessageWithoutSecrets(int status)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("fake-secret", status: (HttpStatusCode)status); fake.Add("fake-secret", status: (HttpStatusCode)status);
        var service = new SpotifyService(Settings(), http, new(root.Root));
        var error = await Assert.ThrowsAsync<ServiceException>(() => service.GetCollectionAsync("playlist123"));
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Assert.DoesNotContain("fake-secret", error.ToString());
        Assert.NotNull(await service.TestConnectionAsync());
    }

    [Fact]
    public async Task MissingCredentialsReturnConnectionMessageAndPublicModeRejectsPersonalCharts()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client();
        var service = new SpotifyService(new AppSettings(), http, new(root.Root));
        Assert.False(service.IsConfigured); Assert.NotNull(await service.TestConnectionAsync());
        await Assert.ThrowsAsync<ServiceException>(() => new SpotifyService(Settings(), http, new(root.Root)).GetCollectionAsync("spotify:liked"));
        Assert.Equal(0, fake.Requests);
    }
}
