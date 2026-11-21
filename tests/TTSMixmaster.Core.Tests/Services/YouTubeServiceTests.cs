using System.Net;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

public sealed class YouTubeServiceTests
{
    private static AppSettings Settings() => new() { YoutubeApiKey = "fake-key", YoutubeChannelId = "channel" };
    private const string Metadata = """{"items":[{"id":"PLtest","snippet":{"title":"A &amp; B","description":"Music","channelTitle":"Owner","thumbnails":{"medium":{"url":"https://image.test/a"}}},"contentDetails":{"itemCount":3}}]}""";

    [Fact]
    public async Task ListsAllChannelPagesAndMapsSpecialEntriesToPlaylists()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"items":[{"id":"PLone","snippet":{"title":"One"}}],"nextPageToken":"next page"}""", r => Assert.Equal("channel", FakeHttp.Query(r.RequestUri!)["channelId"]));
        fake.Add(Metadata, r => Assert.Equal("next page", FakeHttp.Query(r.RequestUri!)["pageToken"]));
        var result = await new YouTubeService(Settings(), http).GetMyCollectionsAsync();
        Assert.Equal(new[] { "PLone", "PLtest", "LL", "WL" }, result.Select(x => x.Id));
        Assert.All(result, x => Assert.Equal(CollectionKind.Playlist, x.Kind));
        Assert.Equal(3, result[1].TrackCount);
        Assert.Equal("A & B", result[1].Name);
        Assert.Contains("not supported", result[2].Description);
    }

    [Theory]
    [InlineData("PLtest")]
    [InlineData("https://youtube.com/playlist?list=PLtest")]
    [InlineData("https://www.youtube.com/playlist?list=PLtest&si=share")]
    [InlineData("https://music.youtube.com/playlist?si=share&list=PLtest")]
    public async Task LoadsAllPagesFromIdOrUrlAndBatchesVideoDetails(string id)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add(Metadata, r => Assert.Equal("PLtest", FakeHttp.Query(r.RequestUri!)["id"]));
        fake.Add("""{"items":[{"contentDetails":{"videoId":"video1"},"snippet":{"title":"Artist - Song (Official Video)","videoOwnerChannelTitle":"Artist - Topic","position":0}},{"contentDetails":{"videoId":"gone"},"snippet":{"title":"Deleted video","position":1}}],"nextPageToken":"page2"}""");
        fake.Add("""{"items":[{"id":"video1","contentDetails":{"duration":"PT3M2S"}}]}""", r =>
        {
            Assert.EndsWith("/videos", r.RequestUri!.AbsolutePath); Assert.Equal("video1,gone", FakeHttp.Query(r.RequestUri!)["id"]);
        });
        fake.Add("""{"items":[{"contentDetails":{"videoId":"video2"},"snippet":{"title":"Second by Singer [Official Audio]","position":2}}]}""", r => Assert.Equal("page2", FakeHttp.Query(r.RequestUri!)["pageToken"]));
        fake.Add("""{"items":[{"id":"video2","contentDetails":{"duration":"bad"}}]}""");
        var result = await new YouTubeService(Settings(), http).GetCollectionAsync(id);
        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal("Artist", result.Tracks[0].Artist); Assert.Equal("Song", result.Tracks[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(182), result.Tracks[0].Duration);
        Assert.Equal("https://www.youtube.com/watch?v=video1", result.Tracks[0].SourceUrl);
        Assert.Equal("Singer", result.Tracks[1].Artist); Assert.Equal("Second", result.Tracks[1].Title);
        Assert.Equal(3, result.Tracks[1].TrackNumber);
        Assert.Equal(5, fake.Requests); // no per-track search
    }

    [Fact]
    public async Task PlaylistSearchPagesPrefersOfficialAndAlbumSearchIsEmpty()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"items":[{"id":{"playlistId":"PLtopic"},"snippet":{"title":"Topic","channelTitle":"Artist - Topic"}}],"nextPageToken":"more"}""", r => Assert.Equal("playlist", FakeHttp.Query(r.RequestUri!)["type"]));
        fake.Add("""{"items":[{"id":{"playlistId":"PLofficial"},"snippet":{"title":"Official","channelTitle":"Artist Official"}}]}""");
        var service = new YouTubeService(Settings(), http);
        var result = await service.SearchAsync("artist", CollectionKind.Playlist, 1);
        Assert.Equal("PLofficial", Assert.Single(result).Id);
        Assert.Empty(await service.SearchAsync("artist", CollectionKind.Album));
        Assert.Empty(await service.SearchAsync("artist", CollectionKind.Chart));
        Assert.Equal(2, fake.Requests);
    }

    [Theory]
    [InlineData("LL", "authorization")]
    [InlineData("WL", "Watch Later")]
    [InlineData("https://evil.test/playlist?list=PLtest", "URL")]
    [InlineData("https://youtube.com/watch?v=video", "URL")]
    [InlineData("https://youtube.com/playlist", "valid")]
    public async Task UnsupportedOrInvalidCollectionsHaveUsefulErrors(string id, string expected)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        var error = await Assert.ThrowsAsync<ServiceException>(() => new YouTubeService(Settings(), http).GetCollectionAsync(id));
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.Requests);
    }

    [Fact]
    public async Task MissingPlaylistIsNotReportedAsAnEmptySuccess()
    {
        using var fake = new FakeHttp(); using var http = fake.Client(); fake.Add("""{"items":[]}""");
        var error = await Assert.ThrowsAsync<ServiceException>(() => new YouTubeService(Settings(), http).GetCollectionAsync("PLmissing"));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task HttpErrorsThrowButConnectionReturnsSafeMessage(int status)
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("fake-key", status: (HttpStatusCode)status); fake.Add("fake-key", status: (HttpStatusCode)status);
        var service = new YouTubeService(Settings(), http);
        var error = await Assert.ThrowsAsync<ServiceException>(() => service.GetMyCollectionsAsync());
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain("fake-key", error.ToString());
        Assert.NotNull(await service.TestConnectionAsync());
    }

    [Fact]
    public async Task RepeatedPageTokenFailsInsteadOfLoopingForever()
    {
        using var fake = new FakeHttp(); using var http = fake.Client();
        fake.Add("""{"items":[],"nextPageToken":"same"}"""); fake.Add("""{"items":[],"nextPageToken":"same"}""");
        await Assert.ThrowsAsync<ServiceException>(() => new YouTubeService(Settings(), http).GetMyCollectionsAsync());
        Assert.Equal(2, fake.Requests);
    }
}
