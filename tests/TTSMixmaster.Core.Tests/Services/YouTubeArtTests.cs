using System.Text.Json;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

namespace TTSMixmaster.Core.Tests.Services;

/// <summary>YouTube thumbnail size preference and "service default" playlist art detection.</summary>
[TestClass]
public sealed class YouTubeArtTests
{
    private static AppSettings Settings() => new() { YoutubeApiKey = "fake-key" };

    private static string Thumbs(string video, params string[] sizes) => JsonSerializer.Serialize(sizes.ToDictionary(
        size => size,
        size => new { url = $"https://i.ytimg.com/vi/{video}/{size}default.jpg" }));

    private static async Task<MusicCollection> LoadAsync(string id, string playlistThumbnails, string firstVideo = "video1")
    {
        using var fake = new FakeHttp();
        using var http = fake.Client();
        fake.Add($$$"""{"items":[{"id":"{{{id}}}","snippet":{"title":"Mix","channelTitle":"Owner","thumbnails":{{{playlistThumbnails}}}},"contentDetails":{"itemCount":2}}]}""");
        fake.Add($$$"""
            {"items":[
              {"contentDetails":{"videoId":"{{{firstVideo}}}"},"snippet":{"title":"A - One","position":0,"thumbnails":{{{Thumbs(firstVideo, "default", "medium", "high")}}}}},
              {"contentDetails":{"videoId":"video2"},"snippet":{"title":"B - Two","position":1,"thumbnails":{{{Thumbs("video2", "default", "medium", "high", "standard", "maxres")}}}}}
            ]}
            """);
        fake.Add("""{"items":[]}""");
        return await new YouTubeService(Settings(), http).GetCollectionAsync(id);
    }

    [TestMethod]
    public async Task A_playlist_thumbnail_of_the_first_video_at_another_size_is_the_service_default()
    {
        var collection = await LoadAsync("PLmix", Thumbs("video1", "default", "medium", "high", "standard", "maxres"));

        Assert.AreEqual(CollectionArtKind.ServiceDefault, collection.Summary.ArtKind);
        Assert.AreEqual("https://i.ytimg.com/vi/video1/maxresdefault.jpg", collection.Summary.ThumbnailUrl);
        Assert.AreEqual("https://i.ytimg.com/vi/video1/highdefault.jpg", collection.Tracks[0].ThumbnailUrl);
    }

    [TestMethod]
    public async Task A_custom_playlist_thumbnail_is_its_own_art()
    {
        var custom = """{"high":{"url":"https://i9.ytimg.com/s_p/PLmix/hqdefault.jpg?sqp=abc"}}""";
        var collection = await LoadAsync("PLmix", custom);

        Assert.AreEqual(CollectionArtKind.Own, collection.Summary.ArtKind);
        Assert.AreEqual("https://i9.ytimg.com/s_p/PLmix/hqdefault.jpg?sqp=abc", collection.Summary.ThumbnailUrl);
    }

    [TestMethod]
    public async Task A_thumbnail_of_a_later_video_is_its_own_art()
    {
        var collection = await LoadAsync("PLmix", Thumbs("video2", "high"));
        Assert.AreEqual(CollectionArtKind.Own, collection.Summary.ArtKind);
    }

    [TestMethod]
    public async Task An_album_playlist_keeps_its_thumbnail_as_its_own_art()
    {
        var collection = await LoadAsync("OLAK5uy_album", Thumbs("video1", "high"));

        Assert.AreEqual(CollectionKind.Album, collection.Summary.Kind);
        Assert.AreEqual(CollectionArtKind.Own, collection.Summary.ArtKind);
    }

    [TestMethod]
    [DataRow("default,medium,high,standard,maxres", "maxres")]
    [DataRow("default,medium,high,standard", "standard")]
    [DataRow("default,medium,high", "high")]
    [DataRow("default,medium", "medium")]
    [DataRow("default", "default")]
    public void The_largest_listed_thumbnail_is_preferred(string sizes, string expected)
    {
        using var snippet = JsonDocument.Parse($$$"""{"thumbnails":{{{Thumbs("v", sizes.Split(','))}}}}""");
        Assert.AreEqual($"https://i.ytimg.com/vi/v/{expected}default.jpg", YouTubeService.Thumbnail(snippet.RootElement));
    }

    [TestMethod]
    [DataRow("https://i.ytimg.com/vi/abc-_123/hqdefault.jpg", "abc-_123")]
    [DataRow("https://i.ytimg.com/vi_webp/abc/maxresdefault.webp", "abc")]
    [DataRow("https://img.youtube.com/vi/abc/0.jpg", "abc")]
    [DataRow("https://i9.ytimg.com/s_p/PLmix/hqdefault.jpg", null)]
    [DataRow("https://i.scdn.co/image/vi/abc/x.jpg", null)]
    [DataRow("https://i.ytimg.com/vi/abc", null)]
    [DataRow("", null)]
    public void Thumbnail_video_ids_come_from_the_vi_path(string url, string? expected) =>
        Assert.AreEqual(expected, YouTubeService.ThumbnailVideoId(url));

    [TestMethod]
    public void Default_detection_compares_video_ids_not_urls()
    {
        Assert.IsTrue(YouTubeService.IsDefaultThumbnail("https://i.ytimg.com/vi/x/maxresdefault.jpg", "https://i.ytimg.com/vi/x/mqdefault.jpg"));
        Assert.IsFalse(YouTubeService.IsDefaultThumbnail("https://i.ytimg.com/vi/x/maxresdefault.jpg", "https://i.ytimg.com/vi/y/mqdefault.jpg"));
        Assert.IsFalse(YouTubeService.IsDefaultThumbnail("", "https://i.ytimg.com/vi/x/mqdefault.jpg"));
        Assert.IsFalse(YouTubeService.IsDefaultThumbnail("https://i.ytimg.com/vi/x/maxresdefault.jpg", null));
    }
}
