using System.Net;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

[TestClass]
public sealed class YouTubeServiceTests
{
    private static AppSettings Settings() => new() { YoutubeApiKey = "fake-key", YoutubeChannelId = "channel" };
    private const string Metadata = """{"items":[{"id":"PLtest","snippet":{"title":"A &amp; B","description":"Music","channelTitle":"Owner","thumbnails":{"medium":{"url":"https://image.test/a"}}},"contentDetails":{"itemCount":3}}]}""";

    [TestMethod]
    public async Task ListsAllChannelPagesAndMapsSpecialEntriesToPlaylists()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"items":[{"id":"PLone","snippet":{"title":"One"}}],"nextPageToken":"next page"}""", r => Assert.AreEqual("channel", FakeHttp.Query(r.RequestUri!)["channelId"]));
        fake.Add(Metadata, r => Assert.AreEqual("next page", FakeHttp.Query(r.RequestUri!)["pageToken"]));
        var result = await new YouTubeService(Settings(), http).GetMyCollectionsAsync();
        Assert.AreSequenceEqual(new[] { "PLone", "PLtest", "LL", "WL" }, result.Select(x => x.Id));
        foreach (var x in result) Assert.AreEqual(CollectionKind.Playlist, x.Kind);
        Assert.AreEqual(3, result[1].TrackCount);
        Assert.AreEqual("A & B", result[1].Name);
        Assert.Contains("not supported", result[2].Description);
    }

    [TestMethod]
    [DataRow("PLtest")]
    [DataRow("https://youtube.com/playlist?list=PLtest")]
    [DataRow("https://www.youtube.com/playlist?list=PLtest&si=share")]
    [DataRow("https://music.youtube.com/playlist?si=share&list=PLtest")]
    public async Task LoadsAllPagesFromIdOrUrlAndBatchesVideoDetails(string id)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(Metadata, r => Assert.AreEqual("PLtest", FakeHttp.Query(r.RequestUri!)["id"]));
        fake.Add("""{"items":[{"contentDetails":{"videoId":"video1"},"snippet":{"title":"Artist - Song (Official Video)","videoOwnerChannelTitle":"Artist - Topic","position":0}},{"contentDetails":{"videoId":"gone"},"snippet":{"title":"Deleted video","position":1}}],"nextPageToken":"page2"}""");
        fake.Add("""{"items":[{"id":"video1","contentDetails":{"duration":"PT3M2S"}}]}""", r =>
        {
            Assert.EndsWith("/videos", r.RequestUri!.AbsolutePath); Assert.AreEqual("video1,gone", FakeHttp.Query(r.RequestUri!)["id"]);
        });
        fake.Add("""{"items":[{"contentDetails":{"videoId":"video2"},"snippet":{"title":"Second by Singer [Official Audio]","position":2}}]}""", r => Assert.AreEqual("page2", FakeHttp.Query(r.RequestUri!)["pageToken"]));
        fake.Add("""{"items":[{"id":"video2","contentDetails":{"duration":"bad"}}]}""");
        var result = await new YouTubeService(Settings(), http).GetCollectionAsync(id);
        Assert.AreEqual(2, result.Tracks.Count);
        Assert.AreEqual("Artist", result.Tracks[0].Artist); Assert.AreEqual("Song", result.Tracks[0].Title);
        Assert.AreEqual(TimeSpan.FromSeconds(182), result.Tracks[0].Duration);
        Assert.AreEqual("https://www.youtube.com/watch?v=video1", result.Tracks[0].SourceUrl);
        Assert.AreEqual("Singer", result.Tracks[1].Artist); Assert.AreEqual("Second", result.Tracks[1].Title);
        Assert.AreEqual(3, result.Tracks[1].TrackNumber);
        Assert.AreEqual(5, fake.Requests); // no per-track search
    }

    [TestMethod]
    public async Task PlaylistSearchPagesPrefersOfficialAndChartSearchIsEmpty()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"items":[{"id":{"playlistId":"PLtopic"},"snippet":{"title":"Topic","channelTitle":"Artist - Topic"}}],"nextPageToken":"more"}""", r => Assert.AreEqual("playlist", FakeHttp.Query(r.RequestUri!)["type"]));
        fake.Add("""{"items":[{"id":{"playlistId":"PLofficial"},"snippet":{"title":"Official","channelTitle":"Artist Official"}}]}""");
        var service = new YouTubeService(Settings(), http);
        var result = await service.SearchAsync("artist", CollectionKind.Playlist, 1);
        Assert.AreEqual("PLofficial", Assert.ContainsSingle(result).Id);
        Assert.IsEmpty(await service.SearchAsync("artist", CollectionKind.Chart));
        Assert.AreEqual(2, fake.Requests);
    }

    [TestMethod]
    [DataRow("LL", "authorization")]
    [DataRow("WL", "Watch Later")]
    [DataRow("https://evil.test/playlist?list=PLtest", "URL")]
    [DataRow("https://youtube.com/watch?v=video", "URL")]
    [DataRow("https://youtube.com/playlist", "valid")]
    public async Task UnsupportedOrInvalidCollectionsHaveUsefulErrors(string id, string expected)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new YouTubeService(Settings(), http).GetCollectionAsync(id));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(0, fake.Requests);
    }

    [TestMethod]
    public async Task MissingPlaylistIsNotReportedAsAnEmptySuccess()
    {
        using var fake = new FakeHttp(); using var http = fake.Client(); fake.Add("""{"items":[]}""");
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => new YouTubeService(Settings(), http).GetCollectionAsync("PLmissing"));
        Assert.AreEqual(HttpStatusCode.NotFound, error.StatusCode);
    }

    [TestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(429)]
    public async Task HttpErrorsThrowButConnectionReturnsSafeMessage(int status)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("fake-key", status: (HttpStatusCode)status); fake.Add("fake-key", status: (HttpStatusCode)status);
        var service = new YouTubeService(Settings(), http);
        var error = await Assert.ThrowsExactlyAsync<ServiceException>(() => service.GetMyCollectionsAsync());
        Assert.AreEqual((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain("fake-key", error.ToString());
        Assert.IsNotNull(await service.TestConnectionAsync());
    }

    [TestMethod]
    public async Task RepeatedPageTokenFailsInsteadOfLoopingForever()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"items":[],"nextPageToken":"same"}"""); fake.Add("""{"items":[],"nextPageToken":"same"}""");
        await Assert.ThrowsExactlyAsync<ServiceException>(() => new YouTubeService(Settings(), http).GetMyCollectionsAsync());
        Assert.AreEqual(2, fake.Requests);
    }
}
