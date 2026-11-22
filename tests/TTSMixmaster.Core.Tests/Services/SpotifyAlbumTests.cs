using System.Net;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;
using static TTSMixmaster.Core.Tests.Services.SpotifyServiceTests;

namespace TTSMixmaster.Core.Tests.Services;

[TestClass]
public sealed class SpotifyAlbumTests
{
    // Shapes follow https://developer.spotify.com/documentation/web-api/reference/get-an-album (trimmed).
    private const string Album = """
        {"id":"album123","type":"album","name":"Big Album","release_date":"2019-05-03","total_tracks":3,
         "artists":[{"name":"Band"},{"name":"Guest"}],
         "images":[{"url":"https://image.test/300","width":300},{"url":"https://image.test/640","width":640},{"url":"https://image.test/64","width":64}]}
        """;

    private static string AlbumTrack(string id, int disc, int number) =>
        $$"""{"type":"track","id":"{{id}}","name":"Song {{id}}","disc_number":{{disc}},"track_number":{{number}},"duration_ms":200000,"artists":[{"name":"Band"}]}""";

    private static async Task<AppPaths> SignedInAsync(TemporaryData root)
    {
        var paths = new AppPaths(root.Root);
        await new SpotifyTokenStore(paths).SaveAsync("fake-client", "fake-refresh");
        return paths;
    }

    [TestMethod]
    public async Task SignedInListingIncludesSavedAlbumsAcrossPages()
    {
        using var root = new TemporaryData(); var paths = await SignedInAsync(root);
        using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("""{"items":[],"next":null}""", r => Assert.EndsWith("/me/playlists", r.RequestUri!.AbsolutePath));
        fake.Add("{\"items\":[{\"added_at\":\"2024-01-01T00:00:00Z\",\"album\":" + Album + "},{\"album\":null}],\"next\":\"https://api.spotify.com/v1/me/albums?offset=50&limit=50\"}",
            r => Assert.EndsWith("/me/albums", r.RequestUri!.AbsolutePath));
        fake.Add("""{"items":[{"album":{"id":"second","name":"Second","artists":[{"name":"Solo"}]}}],"next":null}""", r => Assert.AreEqual("50", FakeHttp.Query(r.RequestUri!)["offset"]));

        var albums = (await new SpotifyService(Settings(true), http, paths).GetMyCollectionsAsync()).Where(x => x.Kind == CollectionKind.Album).ToList();

        Assert.AreSequenceEqual(new[] { "spotify:album:album123", "spotify:album:second" }, albums.Select(x => x.Id));
        Assert.AreEqual("Big Album", albums[0].Name);
        Assert.AreEqual("Band, Guest", albums[0].Owner);
        Assert.AreEqual("https://image.test/640", albums[0].ThumbnailUrl, "the largest image is the chip art");
        Assert.AreEqual(3, albums[0].TrackCount);
        Assert.AreEqual("Released 2019", albums[0].Description);
        Assert.AreEqual(0, fake.Remaining);
    }

    [TestMethod]
    public async Task PublicModeDoesNotAskForSavedAlbums()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("""{"items":[],"next":null}""", r => Assert.EndsWith("/users/listener/playlists", r.RequestUri!.AbsolutePath));
        Assert.IsEmpty(await new SpotifyService(Settings(), http, new(root.Root)).GetMyCollectionsAsync());
        Assert.AreEqual(2, fake.Requests);
    }

    [TestMethod]
    public async Task AlbumSearchUsesAlbumTypeAndMapsAlbums()
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("{\"albums\":{\"items\":[null," + Album + "],\"next\":\"https://api.spotify.com/v1/search?offset=10&type=album&q=big\"}}", r =>
        {
            Assert.AreEqual("album", FakeHttp.Query(r.RequestUri!)["type"]);
            Assert.AreEqual("big album", FakeHttp.Query(r.RequestUri!)["q"]);
        });
        fake.Add("""{"albums":{"items":[{"id":"two","name":"Two","artists":[{"name":"Other"}]},{"id":"three","name":"Three"}],"next":null}}""");

        var result = await new SpotifyService(Settings(), http, new(root.Root)).SearchAsync("big album", CollectionKind.Album, 2);

        Assert.AreSequenceEqual(new[] { "spotify:album:album123", "spotify:album:two" }, result.Select(x => x.Id));
        foreach (var x in result) Assert.AreEqual(CollectionKind.Album, x.Kind);
        Assert.AreEqual("Other", result[1].Owner);
    }

    [TestMethod]
    [DataRow("spotify:album:album123")]
    [DataRow("https://open.spotify.com/album/album123")]
    [DataRow("https://open.spotify.com/album/album123?si=share")]
    [DataRow("https://open.spotify.com/intl-de/album/album123?si=share")]
    public async Task AlbumIdsAndShareUrlsLoadEveryDiscInOrder(string id)
    {
        using var root = new TemporaryData(); using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add(Album, r => Assert.AreEqual("/v1/albums/album123", r.RequestUri!.AbsolutePath));
        fake.Add("{\"items\":[" + AlbumTrack("d1t2", 1, 2) + "," + AlbumTrack("d1t1", 1, 1) + "],\"next\":\"https://api.spotify.com/v1/albums/album123/tracks?offset=50&limit=50\"}",
            r => Assert.AreEqual("/v1/albums/album123/tracks", r.RequestUri!.AbsolutePath));
        fake.Add("{\"items\":[" + AlbumTrack("d2t1", 2, 1) + ",{\"type\":\"track\",\"is_local\":true,\"name\":\"Local\"}],\"next\":null}",
            r => Assert.AreEqual("50", FakeHttp.Query(r.RequestUri!)["offset"]));

        var album = await new SpotifyService(Settings(), http, new(root.Root)).GetCollectionAsync(id);

        Assert.AreEqual(CollectionKind.Album, album.Summary.Kind);
        Assert.AreEqual("spotify:album:album123", album.Summary.Id, "the loaded id matches the listed id, so both use one library folder");
        Assert.AreEqual("Band, Guest", album.Summary.Owner);
        Assert.AreEqual(3, album.Summary.TrackCount);
        Assert.AreSequenceEqual(new[] { "d1t1", "d1t2", "d2t1" }, album.Tracks.Select(x => x.ServiceId));
        Assert.AreSequenceEqual(new int?[] { 1, 2, 3 }, album.Tracks.Select(x => x.TrackNumber));
        foreach (var track in album.Tracks)
        {
            Assert.AreEqual("Big Album", track.Album);
            Assert.AreEqual("Band", track.Artist);
            Assert.AreEqual("https://image.test/640", track.ThumbnailUrl);
            Assert.IsEmpty(track.SourceUrl);
        }
    }

    [TestMethod]
    public async Task SavedAlbumsWithoutPermissionForgetsSignInAndSaysToConnectAgain()
    {
        using var root = new TemporaryData(); var paths = await SignedInAsync(root);
        using var fake = new FakeHttp(); using var http = fake.Client(); Token(fake);
        fake.Add("""{"items":[],"next":null}""");
        fake.Add("""{"error":{"status":403,"message":"Insufficient client scope"}}""", status: HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new SpotifyService(Settings(true), http, paths).GetMyCollectionsAsync());

        Assert.Contains("saved albums", error.Message);
        Assert.Contains("Connect Spotify again", error.Message);
        Assert.DoesNotContain("Insufficient", error.Message);
        Assert.IsNull(await new SpotifyTokenStore(paths).LoadAsync("fake-client"), "the old sign-in is dropped so the next connection prompts again");
    }

    [TestMethod]
    public async Task RefreshMissingANewScopeForgetsSignInBeforeAnyRequest()
    {
        using var root = new TemporaryData(); var paths = await SignedInAsync(root);
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"access_token":"old","expires_in":3600,"scope":"playlist-read-private playlist-read-collaborative user-top-read user-read-recently-played"}""");

        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new SpotifyService(Settings(true), http, paths).GetMyCollectionsAsync());

        Assert.Contains("sign in again", error.Message);
        Assert.AreEqual(1, fake.Requests);
        Assert.IsNull(await new SpotifyTokenStore(paths).LoadAsync("fake-client"));
    }

    [TestMethod]
    public async Task RefreshWithEveryScopeIsKept()
    {
        using var root = new TemporaryData(); var paths = await SignedInAsync(root);
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"access_token":"ok","expires_in":3600,"scope":"user-read-recently-played user-top-read user-library-read playlist-read-collaborative playlist-read-private"}""");
        fake.Add("""{"id":"listener"}""");
        Assert.IsNull(await new SpotifyService(Settings(true), http, paths).TestConnectionAsync());
        Assert.AreEqual("fake-refresh", await new SpotifyTokenStore(paths).LoadAsync("fake-client"));
    }
}
