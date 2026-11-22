using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

[TestClass]
public sealed class LastFmAlbumTests
{
    private static AppSettings Settings() => new() { LastfmApiKey = "fake-key", LastfmUsername = "listener" };

    // Recorded shape of album.getInfo (https://www.last.fm/api/show/album.getInfo), trimmed.
    private const string AlbumInfo = """
        {"album":{"name":"Discovery","artist":"Daft Punk","mbid":"","url":"https://www.last.fm/music/Daft+Punk/Discovery",
          "image":[{"#text":"https://img.test/s.png","size":"small"},{"#text":"https://img.test/xl.png","size":"extralarge"},{"#text":"","size":"mega"}],
          "tracks":{"track":[
            {"name":"Aerodynamic","duration":212,"@attr":{"rank":2},"artist":{"name":"Daft Punk"}},
            {"name":"One More Time","duration":320,"@attr":{"rank":1},"artist":{"name":"Daft Punk"}},
            {"name":"Guest Song","duration":null,"@attr":{"rank":3},"artist":{"name":"Daft Punk feat. Guest"}}]}}}
        """;

    [TestMethod]
    public void AlbumIdsEscapeTheirValues() =>
        Assert.AreEqual("lastfm:album?artist=AC%2FDC&album=Back%20in%20Black%20%26%20More", LastFmService.CreateAlbumId("AC/DC", "Back in Black & More"));

    [TestMethod]
    [DataRow("lastfm:album?artist=Daft%20Punk&album=Discovery")]
    [DataRow("https://www.last.fm/music/Daft+Punk/Discovery")]
    [DataRow("https://last.fm/music/Daft%20Punk/Discovery?ref=share")]
    public async Task AlbumGetInfoMapsTracksInRankOrder(string id)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(AlbumInfo, r =>
        {
            var q = FakeHttp.Query(r.RequestUri!);
            Assert.AreEqual("album.getinfo", q["method"]); Assert.AreEqual("Daft Punk", q["artist"]); Assert.AreEqual("Discovery", q["album"]);
        });

        var album = await new LastFmService(Settings(), http).GetCollectionAsync(id);

        Assert.AreEqual(CollectionKind.Album, album.Summary.Kind);
        Assert.AreEqual(LastFmService.CreateAlbumId("Daft Punk", "Discovery"), album.Summary.Id);
        Assert.AreEqual("Discovery", album.Summary.Name);
        Assert.AreEqual("Daft Punk", album.Summary.Owner);
        Assert.AreEqual("https://img.test/xl.png", album.Summary.ThumbnailUrl);
        Assert.AreSequenceEqual(new[] { "One More Time", "Aerodynamic", "Guest Song" }, album.Tracks.Select(x => x.Title));
        Assert.AreSequenceEqual(new int?[] { 1, 2, 3 }, album.Tracks.Select(x => x.TrackNumber));
        Assert.AreEqual(TimeSpan.FromSeconds(320), album.Tracks[0].Duration);
        Assert.AreEqual("Daft Punk feat. Guest", album.Tracks[2].Artist);
        foreach (var track in album.Tracks) { Assert.AreEqual("Discovery", track.Album); Assert.IsEmpty(track.SourceUrl); }
    }

    [TestMethod]
    public async Task SingleTrackAlbumObjectIsAccepted()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"album":{"name":"Single","artist":"Solo","tracks":{"track":{"name":"Only","duration":100}}}}""");
        var album = await new LastFmService(Settings(), http).GetCollectionAsync(LastFmService.CreateAlbumId("Solo", "Single"));
        var track = Assert.ContainsSingle(album.Tracks);
        Assert.AreEqual("Solo", track.Artist); Assert.AreEqual(1, track.TrackNumber);
    }

    [TestMethod]
    public async Task AlbumWithoutATrackListIsAnErrorNotAnEmptyAlbum()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"album":{"name":"Obscure","artist":"Nobody","tracks":{}}}""");
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new LastFmService(Settings(), http).GetCollectionAsync(LastFmService.CreateAlbumId("Nobody", "Obscure")));
        Assert.Contains("no track list", error.Message);
    }

    [TestMethod]
    [DataRow("lastfm:album?artist=Solo")]
    [DataRow("lastfm:album?album=Single")]
    [DataRow("https://www.last.fm/music/Artist/_/Track")]
    [DataRow("https://www.last.fm/music/Artist/+albums")]
    public async Task IncompleteAlbumIdsFailBeforeNetwork(string id)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        await Assert.ThrowsExactlyAsync<ServiceException>(() => new LastFmService(Settings(), http).GetCollectionAsync(id));
        Assert.AreEqual(0, fake.Requests);
    }

    [TestMethod]
    public async Task AlbumSearchPagesAndMapsOpenableAlbums()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""
            {"results":{"opensearch:totalResults":"300","albummatches":{"album":[
              {"name":"Discovery","artist":"Daft Punk","image":[{"#text":"https://img.test/s.png"},{"#text":"https://img.test/xl.png"}]},
              {"name":"","artist":"Broken"}]}}}
            """, r =>
        {
            var q = FakeHttp.Query(r.RequestUri!);
            Assert.AreEqual("album.search", q["method"]); Assert.AreEqual("disco", q["album"]); Assert.AreEqual("2", q["limit"]); Assert.AreEqual("1", q["page"]);
            Assert.IsFalse(q.ContainsKey("user"));
        });
        fake.Add("""{"results":{"opensearch:totalResults":"300","albummatches":{"album":[{"name":"Homework","artist":"Daft Punk"},{"name":"Alive","artist":"Daft Punk"}]}}}""",
            r => Assert.AreEqual("2", FakeHttp.Query(r.RequestUri!)["page"]));

        var result = await new LastFmService(Settings(), http).SearchAsync("disco", CollectionKind.Album, 2);

        Assert.AreSequenceEqual(new[] { "Discovery", "Homework" }, result.Select(x => x.Name));
        Assert.AreEqual(LastFmService.CreateAlbumId("Daft Punk", "Discovery"), result[0].Id);
        Assert.AreEqual("Daft Punk", result[0].Owner);
        Assert.AreEqual("https://img.test/xl.png", result[0].ThumbnailUrl);
        foreach (var x in result) Assert.AreEqual(CollectionKind.Album, x.Kind);
    }

    [TestMethod]
    [DataRow("overall", "all time")]
    [DataRow("7day", "last 7 days")]
    [DataRow("12month", "last year")]
    public async Task TopAlbumsUseTheChartPeriodAndOpenAsAlbums(string period, string label)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""
            {"topalbums":{"album":[
              {"name":"Discovery","playcount":"1234","artist":{"name":"Daft Punk"},"@attr":{"rank":"1"}},
              {"name":"Homework","playcount":"5","artist":{"name":"Daft Punk"},"@attr":{"rank":"2"}}],"@attr":{"totalPages":"1"}}}
            """, r =>
        {
            var q = FakeHttp.Query(r.RequestUri!);
            Assert.AreEqual("user.gettopalbums", q["method"]); Assert.AreEqual(period, q["period"]); Assert.AreEqual("listener", q["user"]);
        });

        var albums = await new LastFmService(Settings(), http).GetTopAlbumsAsync(period, 10);

        Assert.AreEqual(2, albums.Count);
        Assert.AreEqual(LastFmService.CreateAlbumId("Daft Punk", "Discovery"), albums[0].Id);
        Assert.AreEqual(CollectionKind.Album, albums[0].Kind);
        Assert.AreEqual($"#1 in listener's top albums ({label}) · {1234:N0} plays", albums[0].Description);
        Assert.StartsWith("#2", albums[1].Description);
    }

    [TestMethod]
    public async Task TopAlbumsRejectAnUnknownPeriodBeforeNetwork()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        await Assert.ThrowsExactlyAsync<ServiceException>(() => new LastFmService(Settings(), http).GetTopAlbumsAsync("week"));
        Assert.AreEqual(0, fake.Requests);
    }
}
