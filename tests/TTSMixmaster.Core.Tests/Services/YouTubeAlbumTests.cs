using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

[TestClass]
public sealed class YouTubeAlbumTests
{
    private const string AlbumId = "OLAK5uy_kAbCdEfGhIjKlMnOpQrStUvWxYz012345";
    private static AppSettings Settings() => new() { YoutubeApiKey = "fake-key", YoutubeChannelId = "channel" };

    // Recorded shape of playlists.list for a YouTube Music album playlist (trimmed): the owner is "YouTube".
    private const string AlbumMetadata = $$$$"""
        {"items":[{"id":"{{{{AlbumId}}}}","snippet":{"title":"Album - Night &amp; Day","description":"","channelTitle":"YouTube",
          "thumbnails":{"medium":{"url":"https://image.test/album"}}},"contentDetails":{"itemCount":3}}]}
        """;

    [TestMethod]
    [DataRow("Album - Night Day", "Night Day")]
    [DataRow("album - Night Day", "Night Day")]
    [DataRow("ALBUM - Night Day", "Night Day")]
    [DataRow("Night Day", "Night Day")]
    [DataRow("Album - ", "Album - ")]
    [DataRow("Albums - Night Day", "Albums - Night Day")]
    public void AlbumPrefixIsStrippedInAnyCase(string title, string expected) =>
        Assert.AreEqual(expected, YouTubeService.AlbumName(title));

    [TestMethod]
    [DataRow(AlbumId)]
    [DataRow("https://music.youtube.com/playlist?list=" + AlbumId)]
    [DataRow("https://music.youtube.com/playlist?list=" + AlbumId + "&si=share")]
    [DataRow("https://www.youtube.com/playlist?list=" + AlbumId)]
    public async Task AlbumPlaylistLoadsAsAnAlbumWithItsArtistAndUnsplitTitles(string id)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(AlbumMetadata, r => Assert.AreEqual(AlbumId, FakeHttp.Query(r.RequestUri!)["id"]));
        fake.Add("""
            {"items":[
              {"contentDetails":{"videoId":"v1"},"snippet":{"title":"Opening","videoOwnerChannelTitle":"The Band - Topic","position":0}},
              {"contentDetails":{"videoId":"v2"},"snippet":{"title":"Song - 2011 Remaster","videoOwnerChannelTitle":"The Band - Topic","position":1}},
              {"contentDetails":{"videoId":"v3"},"snippet":{"title":"Closer","videoOwnerChannelTitle":"The Band - Topic","position":2}}]}
            """);
        fake.Add("""{"items":[{"id":"v1","contentDetails":{"duration":"PT3M"}}]}""");

        var album = await new YouTubeService(Settings(), http).GetCollectionAsync(id);

        Assert.AreEqual(CollectionKind.Album, album.Summary.Kind);
        Assert.AreEqual(AlbumId, album.Summary.Id);
        Assert.AreEqual("Night & Day", album.Summary.Name);
        Assert.AreEqual("The Band", album.Summary.Owner, "the Topic channel names the artist; \"YouTube\" is dropped");
        Assert.AreSequenceEqual(new[] { "Opening", "Song - 2011 Remaster", "Closer" }, album.Tracks.Select(x => x.Title));
        foreach (var track in album.Tracks)
        {
            Assert.AreEqual("The Band", track.Artist);
            Assert.AreEqual("Night & Day", track.Album);
            Assert.StartsWith("https://www.youtube.com/watch?v=", track.SourceUrl);
        }
    }

    [TestMethod]
    public async Task OrdinaryPlaylistsKeepTitleParsingAndNoAlbumName()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"items":[{"id":"PLmix","snippet":{"title":"Album - Mix","channelTitle":"Curator"}}]}""");
        fake.Add("""{"items":[{"contentDetails":{"videoId":"v1"},"snippet":{"title":"Singer - Song","videoOwnerChannelTitle":"Other - Topic","position":0}}]}""");
        fake.Add("""{"items":[]}""");

        var playlist = await new YouTubeService(Settings(), http).GetCollectionAsync("PLmix");

        Assert.AreEqual(CollectionKind.Playlist, playlist.Summary.Kind);
        Assert.AreEqual("Album - Mix", playlist.Summary.Name, "only album playlists lose the prefix");
        Assert.AreEqual("Curator", playlist.Summary.Owner);
        var track = Assert.ContainsSingle(playlist.Tracks);
        Assert.AreEqual("Singer", track.Artist); Assert.AreEqual("Song", track.Title); Assert.IsEmpty(track.Album);
    }

    [TestMethod]
    public async Task ListingClassifiesAlbumPlaylistsAndCleansTopicOwners()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add($$$"""{"items":[{"id":"PLmine","snippet":{"title":"Mine","channelTitle":"Me"}},{"id":"{{{AlbumId}}}","snippet":{"title":"Album - Saved","channelTitle":"Artist - Topic"}}]}""");

        var result = await new YouTubeService(Settings(), http).GetMyCollectionsAsync();

        Assert.AreEqual(CollectionKind.Playlist, result[0].Kind);
        Assert.AreEqual(CollectionKind.Album, result[1].Kind);
        Assert.AreEqual("Saved", result[1].Name);
        Assert.AreEqual("Artist", result[1].Owner);
    }

    [TestMethod]
    public async Task AlbumSearchKeepsOnlyAlbumPlaylistsAndPlaylistSearchExcludesThem()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        var page = $$$"""
            {"items":[
              {"id":{"playlistId":"PLfan"},"snippet":{"title":"Fan mix","channelTitle":"Fan"}},
              {"id":{"playlistId":"{{{AlbumId}}}"},"snippet":{"title":"Album - Real","channelTitle":"YouTube"}}]}
            """;
        fake.Add(page, r =>
        {
            Assert.AreEqual("playlist", FakeHttp.Query(r.RequestUri!)["type"]);
            Assert.AreEqual("real", FakeHttp.Query(r.RequestUri!)["q"]);
        });
        fake.Add(page);
        var service = new YouTubeService(Settings(), http);

        var albums = await service.SearchAsync("real", CollectionKind.Album);
        var playlists = await service.SearchAsync("real", CollectionKind.Playlist);

        var album = Assert.ContainsSingle(albums);
        Assert.AreEqual(AlbumId, album.Id); Assert.AreEqual(CollectionKind.Album, album.Kind); Assert.AreEqual("Real", album.Name); Assert.IsEmpty(album.Owner);
        Assert.AreEqual("PLfan", Assert.ContainsSingle(playlists).Id);
    }

    [TestMethod]
    public async Task AlbumSearchStopsAfterTwoPagesToSaveQuota()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"items":[{"id":{"playlistId":"PLa"},"snippet":{"title":"A"}}],"nextPageToken":"p2"}""");
        fake.Add("""{"items":[{"id":{"playlistId":"PLb"},"snippet":{"title":"B"}}],"nextPageToken":"p3"}""");

        Assert.IsEmpty(await new YouTubeService(Settings(), http).SearchAsync("nothing", CollectionKind.Album));
        Assert.AreEqual(2, fake.Requests);
    }

    [TestMethod]
    public async Task PlaylistSearchThatOnlyFindsAlbumsStopsAfterTwoPagesToSaveQuota()
    {
        // Final review finding 3: every page is album playlists, which Playlist search filters out; with fresh page
        // tokens it used to keep requesting until YouTube ran out of results.
        using var fake = new FakeHttp(); using var http = fake.Client();
        for (var p = 0; p < 9; p++)
        {
            var items = string.Join(",", Enumerable.Range(0, 40).Select(i =>
                $$$"""{"id":{"playlistId":"OLAK5uy_p{{{p}}}i{{{i}}}"},"snippet":{"title":"Album - A{{{i}}}","channelTitle":"YouTube"}}"""));
            fake.Add($$"""{"items":[{{items}}],"nextPageToken":"t{{p + 1}}"}""");
        }

        Assert.IsEmpty(await new YouTubeService(Settings(), http).SearchAsync("an album title", CollectionKind.Playlist));
        Assert.AreEqual(YouTubeService.SearchPages, fake.Requests);
    }

    [TestMethod]
    [DataRow("https://music.youtube.com/browse/MPREb_AbCdEfGhIjK")]
    [DataRow("https://music.youtube.com/browse/MPREb_AbCdEfGhIjK?si=x")]
    public async Task BrowseAlbumLinksExplainHowToGetAPlaylistLink(string url)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new YouTubeService(Settings(), http).GetCollectionAsync(url));
        Assert.Contains("copy the link from Share", error.Message);
        Assert.AreEqual(0, fake.Requests);
    }
}
