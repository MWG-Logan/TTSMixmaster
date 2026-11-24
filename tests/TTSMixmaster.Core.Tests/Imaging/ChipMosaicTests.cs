using System.Collections.Concurrent;
using System.Net;
using SkiaSharp;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;
using static TTSMixmaster.Core.Tests.Imaging.ChipTestImages;

namespace TTSMixmaster.Core.Tests.Imaging;

/// <summary>Chip art for collections without art of their own: a 2x2 mosaic of distinct track covers.</summary>
[TestClass]
public sealed class ChipMosaicTests
{
    private static readonly ChipOptions NoTitle = new() { ShowTitle = false };

    /// <summary>Window pixels at the centre of each mosaic quadrant: top-left, top-right, bottom-left, bottom-right.</summary>
    private static readonly (int X, int Y)[] Quadrants = [(135, 135), (343, 135), (135, 343), (343, 343)];

    private static readonly SKColor[] Colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow, SKColors.Magenta, SKColors.Cyan];

    /// <summary>Serves art by URL, in any order (mosaic covers download in parallel); unknown URLs are 404.</summary>
    private sealed class ArtServer : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _art = new(StringComparer.Ordinal);
        public ConcurrentQueue<string> Requests { get; } = new();
        public Func<string, TimeSpan> Delay { get; set; } = _ => TimeSpan.Zero;

        public ArtServer Serve(string url, byte[] png) { _art[url] = png; return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Enqueue(url);
            await Task.Delay(Delay(url), ct);
            return _art.TryGetValue(url, out var png)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static string Url(int i) => $"https://img.example/cover{i}.png";

    /// <summary>A server with a distinct solid colour at cover0…cover(n-1).</summary>
    private static ArtServer Covers(int count)
    {
        var server = new ArtServer();
        for (var i = 0; i < count; i++) server.Serve(Url(i), Solid(64, 64, Colors[i]));
        return server;
    }

    private static MusicCollection Collection(IEnumerable<string> trackArt, string thumbnail = "",
        CollectionArtKind art = CollectionArtKind.Own, ServiceKind service = ServiceKind.Spotify) => new()
    {
        Summary = new() { Id = "c", Name = "Liked Songs", Service = service, Kind = CollectionKind.Playlist, ThumbnailUrl = thumbnail, ArtKind = art },
        Tracks = [.. trackArt.Select((url, i) => new Track { Title = $"T{i}", Artist = "A", ThumbnailUrl = url })],
    };

    private static void AssertQuadrants(byte[] chipPng, params SKColor[] expected)
    {
        using var output = Decode(chipPng);
        for (var i = 0; i < 4; i++)
            AssertClose(expected[i], output.GetPixel(Quadrants[i].X, Quadrants[i].Y), tolerance: 12, where: $"quadrant {i}");
    }

    [TestMethod]
    public async Task No_art_of_its_own_and_four_distinct_covers_makes_a_2x2_mosaic_in_track_order()
    {
        var server = Covers(5);
        var collection = Collection(Enumerable.Range(0, 5).Select(Url));
        var chip = await new ChipImageBuilder(new HttpClient(server)).BuildFrontAsync(collection, NoTitle);

        Assert.AreEqual(ChipArtSource.Mosaic, chip.ArtSource);
        AssertQuadrants(chip.Png, Colors[..4]);
        Assert.DoesNotContain(Url(4), server.Requests, "only four covers are needed");

        var fetch = await new ChipImageBuilder(new HttpClient(Covers(5))).FetchArtAsync(collection);
        Assert.AreEqual(ChipArtSource.Mosaic, fetch.Source);
        using var mosaic = Decode(fetch.Art!);
        Assert.AreEqual((ChipImageBuilder.MosaicSize, ChipImageBuilder.MosaicSize), (mosaic.Width, mosaic.Height));
        AssertClose(Colors[3], mosaic.GetPixel(768, 768));
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(2)]
    [DataRow(1)]
    public async Task Fewer_than_four_distinct_covers_uses_the_first_alone(int count)
    {
        var chip = await new ChipImageBuilder(new HttpClient(Covers(count)))
            .BuildFrontAsync(Collection(Enumerable.Range(0, count).Select(Url)), NoTitle);

        Assert.AreEqual(ChipArtSource.Track, chip.ArtSource);
        AssertQuadrants(chip.Png, Colors[0], Colors[0], Colors[0], Colors[0]);
    }

    [TestMethod]
    public async Task Repeated_covers_count_once_by_url_and_by_youtube_video_id()
    {
        var server = new ArtServer()
            .Serve("https://i.ytimg.com/vi/aaa/hqdefault.jpg", Solid(64, 64, Colors[0]))
            .Serve("https://i.ytimg.com/vi/aaa/maxresdefault.jpg", Solid(64, 64, SKColors.White))
            .Serve("https://i.ytimg.com/vi/bbb/hqdefault.jpg", Solid(64, 64, Colors[1]))
            .Serve(Url(2), Solid(64, 64, Colors[2]))
            .Serve(Url(3), Solid(64, 64, Colors[3]));
        var collection = Collection(
        [
            "https://i.ytimg.com/vi/aaa/hqdefault.jpg", "https://i.ytimg.com/vi/aaa/hqdefault.jpg",
            "https://i.ytimg.com/vi/aaa/maxresdefault.jpg", "https://i.ytimg.com/vi/bbb/hqdefault.jpg",
            "", Url(2), Url(2), Url(3),
        ], service: ServiceKind.YouTube);

        var chip = await new ChipImageBuilder(new HttpClient(server)).BuildFrontAsync(collection, NoTitle);

        AssertQuadrants(chip.Png, Colors[..4]);
        Assert.HasCount(4, server.Requests);
        Assert.DoesNotContain("https://i.ytimg.com/vi/aaa/maxresdefault.jpg", server.Requests);
    }

    [TestMethod]
    public async Task A_failed_cover_is_skipped_for_the_next_distinct_one()
    {
        var server = Covers(5);
        var collection = Collection(["https://img.example/missing.png", .. Enumerable.Range(0, 5).Select(Url)]);
        server.Serve(Url(1), "<html>not an image</html>"u8.ToArray());

        var chip = await new ChipImageBuilder(new HttpClient(server)).BuildFrontAsync(collection, NoTitle);

        Assert.AreEqual(ChipArtSource.Mosaic, chip.ArtSource);
        AssertQuadrants(chip.Png, Colors[0], Colors[2], Colors[3], Colors[4]);
    }

    [TestMethod]
    public async Task Too_few_covers_left_after_failures_falls_back_to_the_first_good_one()
    {
        var server = Covers(4);
        var collection = Collection(["https://img.example/missing.png", Url(1), Url(2), Url(3)]);

        var chip = await new ChipImageBuilder(new HttpClient(server)).BuildFrontAsync(collection, NoTitle);

        Assert.AreEqual(ChipArtSource.Track, chip.ArtSource);
        AssertQuadrants(chip.Png, Colors[1], Colors[1], Colors[1], Colors[1]);
    }

    [TestMethod]
    public async Task The_same_inputs_render_the_same_png_whatever_order_downloads_finish_in()
    {
        var collection = Collection(["https://img.example/missing.png", .. Enumerable.Range(0, 5).Select(Url)]);
        var first = Covers(5);
        first.Delay = url => TimeSpan.FromMilliseconds(url.EndsWith("0.png", StringComparison.Ordinal) ? 150 : 0);
        var second = Covers(5);
        second.Delay = url => TimeSpan.FromMilliseconds(url.EndsWith("3.png", StringComparison.Ordinal) ? 150 : 0);

        var a = await new ChipImageBuilder(new HttpClient(first)).BuildFrontAsync(collection, new ChipOptions());
        var b = await new ChipImageBuilder(new HttpClient(second)).BuildFrontAsync(collection, new ChipOptions());

        Assert.AreEqual(ChipArtSource.Mosaic, a.ArtSource);
        Assert.AreEqual(a.Hash, b.Hash);
        Assert.AreSequenceEqual(a.Png, b.Png);
    }

    [TestMethod]
    public async Task Own_art_wins_and_no_track_cover_is_downloaded()
    {
        var server = Covers(4).Serve("https://img.example/own.png", Solid(64, 64, SKColors.Orange));
        var chip = await new ChipImageBuilder(new HttpClient(server))
            .BuildFrontAsync(Collection(Enumerable.Range(0, 4).Select(Url), "https://img.example/own.png"), NoTitle);

        Assert.AreEqual(ChipArtSource.Collection, chip.ArtSource);
        AssertQuadrants(chip.Png, SKColors.Orange, SKColors.Orange, SKColors.Orange, SKColors.Orange);
        Assert.AreSequenceEqual(new[] { "https://img.example/own.png" }, server.Requests);
    }

    [TestMethod]
    public async Task A_service_default_thumbnail_is_replaced_by_the_mosaic()
    {
        var server = Covers(4).Serve("https://i.ytimg.com/vi/first/maxresdefault.jpg", Solid(64, 64, SKColors.Orange));
        var collection = Collection(Enumerable.Range(0, 4).Select(Url), "https://i.ytimg.com/vi/first/maxresdefault.jpg",
            CollectionArtKind.ServiceDefault, ServiceKind.YouTube);

        var chip = await new ChipImageBuilder(new HttpClient(server)).BuildFrontAsync(collection, NoTitle);

        Assert.AreEqual(ChipArtSource.Mosaic, chip.ArtSource);
        AssertQuadrants(chip.Png, Colors[..4]);
        Assert.DoesNotContain("https://i.ytimg.com/vi/first/maxresdefault.jpg", server.Requests);
    }

    [TestMethod]
    public async Task A_service_default_thumbnail_is_still_used_when_no_track_cover_loads()
    {
        var server = new ArtServer().Serve("https://i.ytimg.com/vi/first/maxresdefault.jpg", Solid(64, 64, SKColors.Orange));
        var collection = Collection(["https://img.example/missing.png"], "https://i.ytimg.com/vi/first/maxresdefault.jpg",
            CollectionArtKind.ServiceDefault, ServiceKind.YouTube);

        var chip = await new ChipImageBuilder(new HttpClient(server)).BuildFrontAsync(collection, NoTitle);

        Assert.AreEqual(ChipArtSource.Collection, chip.ArtSource);
        AssertQuadrants(chip.Png, SKColors.Orange, SKColors.Orange, SKColors.Orange, SKColors.Orange);
    }

    [TestMethod]
    public async Task Own_art_that_fails_falls_back_to_the_mosaic()
    {
        var chip = await new ChipImageBuilder(new HttpClient(Covers(4)))
            .BuildFrontAsync(Collection(Enumerable.Range(0, 4).Select(Url), "https://img.example/gone.png"), NoTitle);

        Assert.AreEqual(ChipArtSource.Mosaic, chip.ArtSource);
        AssertQuadrants(chip.Png, Colors[..4]);
    }

    [TestMethod]
    public async Task At_most_twelve_covers_are_tried()
    {
        var server = new ArtServer();
        var chip = await new ChipImageBuilder(new HttpClient(server))
            .BuildFrontAsync(Collection(Enumerable.Range(0, 40).Select(Url)), NoTitle);

        Assert.AreEqual(ChipArtSource.Generated, chip.ArtSource);
        Assert.HasCount(ChipImageBuilder.MaxCoverAttempts, server.Requests);
    }

    /// <summary>
    /// YouTube's 4:3 hqdefault of a 16:9 music video: square art (blue, with a yellow border band) pillarboxed in the
    /// 16:9 frame, which is letterboxed in the 4:3 thumbnail. Without trimming, the centre square would show black bars.
    /// </summary>
    internal static byte[] LetterboxedCover(SKColor art) => Png(480, 360, c =>
    {
        c.Clear(SKColors.Black);
        using var band = new SKPaint { Color = SKColors.Yellow };
        using var fill = new SKPaint { Color = art };
        var content = SKRect.Create(105, 45, 270, 270);
        c.DrawRect(content, band);
        content.Inflate(-20, -20);
        c.DrawRect(content, fill);
    });

    [TestMethod]
    public void A_letterboxed_single_cover_fills_the_window_with_its_square_art()
    {
        var chip = ChipImageBuilder.RenderFront(LetterboxedCover(SKColors.Blue), "", ServiceKind.YouTube, NoTitle);
        using var output = Decode(chip.Png);

        AssertClose(SKColors.Blue, output.GetPixel(239, 239), where: "centre");
        foreach (var (x, y) in new[] { (36, 239), (441, 239), (239, 36), (239, 441) })
            AssertClose(SKColors.Yellow, output.GetPixel(x, y), tolerance: 24, where: $"edge ({x},{y}) is the art's border, not a black bar");
    }

    [TestMethod]
    public void Letterboxed_mosaic_tiles_are_trimmed_to_their_square_art()
    {
        var mosaic = ChipImageBuilder.ComposeMosaic([.. Colors[..4].Select(LetterboxedCover)]);
        using var output = Decode(mosaic);

        for (var i = 0; i < 4; i++)
        {
            var (left, top) = (i % 2 * 512, i / 2 * 512);
            AssertClose(Colors[i], output.GetPixel(left + 256, top + 256), where: $"tile {i} centre");
            AssertClose(SKColors.Yellow, output.GetPixel(left + 4, top + 256), tolerance: 24, where: $"tile {i} left edge");
            AssertClose(SKColors.Yellow, output.GetPixel(left + 256, top + 507), tolerance: 24, where: $"tile {i} bottom edge");
        }
    }

    [TestMethod]
    public void A_dark_picture_without_matched_bars_is_only_centre_cropped()
    {
        // Black on the left only (a dark scene, not a pillarbox): nothing is trimmed, the centre square is shown.
        var art = Png(1280, 720, c =>
        {
            c.Clear(SKColors.Red);
            using var black = new SKPaint { Color = SKColors.Black };
            c.DrawRect(SKRect.Create(0, 0, 400, 720), black);
        });
        var chip = ChipImageBuilder.RenderFront(art, "", null, NoTitle);
        using var output = Decode(chip.Png);
        AssertClose(SKColors.Black, output.GetPixel(60, 239), where: "left of the centre square is the dark scene");
        AssertClose(SKColors.Red, output.GetPixel(420, 239), where: "right side");
    }
}
