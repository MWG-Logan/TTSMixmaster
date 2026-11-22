using System.Net;
using System.Security.Cryptography;
using SkiaSharp;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tests.Services;
using static TTSMixmaster.Core.Tests.Imaging.ChipTestImages;

namespace TTSMixmaster.Core.Tests.Imaging;

[TestClass]
public sealed class ChipImageBuilderTests
{
    private static readonly ChipOptions NoTitle = new() { ShowTitle = false };

    private static MusicCollection Collection(string thumbnail = "", string trackThumbnail = "", string name = "Woody's Fine EDM",
        ServiceKind service = ServiceKind.Spotify) => new()
    {
        Summary = new CollectionSummary { Id = "p1", Name = name, Service = service, Kind = CollectionKind.Playlist, ThumbnailUrl = thumbnail },
        Tracks =
        [
            new Track { Title = "No art", Artist = "A" },
            new Track { Title = "Art", Artist = "B", ThumbnailUrl = trackThumbnail },
        ],
    };

    private static HttpResponseMessage PngResponse(byte[] png) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } } };

    [TestMethod]
    public void Same_input_renders_the_same_bytes_and_hash()
    {
        var art = CentreSquare(640, 480);
        var first = ChipImageBuilder.RenderFront(art, "Woody's Fine EDM", ServiceKind.Spotify);
        var second = ChipImageBuilder.RenderFront(art, "Woody's Fine EDM", ServiceKind.Spotify);
        Assert.AreEqual(first.Hash, second.Hash);
        Assert.AreSequenceEqual(first.Png, second.Png);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(first.Png)), first.Hash);
        Assert.AreEqual($"chip-{first.Hash}.png", first.FileName);

        Assert.AreNotEqual(first.Hash, ChipImageBuilder.RenderFront(art, "Another Title", ServiceKind.Spotify).Hash);
        Assert.AreNotEqual(first.Hash, ChipImageBuilder.RenderFront(Solid(64, 64, SKColors.Blue), "Woody's Fine EDM", ServiceKind.Spotify).Hash);
    }

    [TestMethod]
    public void Output_is_a_478_png_with_art_in_the_window_and_the_frame_on_top()
    {
        var chip = ChipImageBuilder.RenderFront(Solid(500, 500, SKColors.Blue), "", null, NoTitle);
        Assert.AreEqual((478, 478), (chip.Width, chip.Height));
        using var output = Decode(chip.Png);
        using var frame = Decode(ChipAssets.FrameBytes);
        Assert.AreEqual((478, 478), (output.Width, output.Height));

        AssertClose(SKColors.Blue, output.GetPixel(239, 239), where: "centre");
        AssertClose(SKColors.Blue, output.GetPixel(100, 440), where: "beside the notch");
        AssertClose(SKColors.Blue, output.GetPixel(33, 33), where: "window top-left");
        Assert.AreEqual(frame.GetPixel(15, 240), output.GetPixel(15, 240)); // frame edge
        Assert.AreEqual(frame.GetPixel(40, 430), output.GetPixel(40, 430)); // notch tab
        Assert.AreEqual(frame.GetPixel(33, 436), output.GetPixel(33, 436)); // ♫ glyph
        Assert.AreEqual(0, output.GetPixel(0, 0).Alpha); // rounded corners stay transparent
        Assert.AreEqual(0, output.GetPixel(477, 477).Alpha);

        // No seam: every pixel the window touches is fully opaque (art under the frame's soft edge).
        for (var y = 0; y < 478; y++)
            for (var x = 0; x < 478; x++)
                if (ChipGeometry.Coverage(x, y) > 0)
                    Assert.IsTrue(output.GetPixel(x, y).Alpha == 255, $"translucent window pixel at ({x},{y})");
    }

    [TestMethod]
    [DataRow(1600, 900)] // wide (16:9)
    [DataRow(900, 1600)] // tall
    [DataRow(480, 360)] // 4:3
    public void Cover_fit_fills_the_window_from_the_centre_of_the_art(int width, int height)
    {
        // Red centre square, green elsewhere: a centre crop shows only red.
        var chip = ChipImageBuilder.RenderFront(CentreSquare(width, height), "", null, NoTitle);
        using var output = Decode(chip.Png);
        foreach (var (x, y) in new[] { (34, 34), (443, 34), (239, 239), (443, 443), (100, 443), (34, 380) })
            AssertClose(SKColors.Red, output.GetPixel(x, y), tolerance: 24, where: $"({x},{y})");
    }

    [TestMethod]
    public void Tiny_art_is_upscaled_to_fill_the_window()
    {
        var chip = ChipImageBuilder.RenderFront(Solid(3, 2, SKColors.Orange), "", null, NoTitle);
        using var output = Decode(chip.Png);
        foreach (var (x, y) in new[] { (34, 34), (239, 239), (443, 443) })
            AssertClose(SKColors.Orange, output.GetPixel(x, y), where: $"({x},{y})");
    }

    [TestMethod]
    public void Cover_crop_takes_the_centre()
    {
        var square = SKRect.Create(0, 0, 100, 100);
        Assert.AreEqual(SKRect.Create(280, 0, 720, 720), ChipArt.CoverCrop(SKRect.Create(0, 0, 1280, 720), square));
        Assert.AreEqual(SKRect.Create(0, 280, 720, 720), ChipArt.CoverCrop(SKRect.Create(0, 0, 720, 1280), square));
        Assert.AreEqual(SKRect.Create(0, 0, 8, 8), ChipArt.CoverCrop(SKRect.Create(0, 0, 8, 8), square));
    }

    [TestMethod]
    public void Letterbox_bars_are_trimmed_before_cropping()
    {
        // A 4:3 thumbnail of a 16:9 video: black bars top and bottom.
        var png = Png(480, 360, c =>
        {
            c.Clear(SKColors.Black);
            using var orange = new SKPaint { Color = SKColors.Orange };
            c.DrawRect(SKRect.Create(0, 45, 480, 270), orange);
        });
        using (var bitmap = ChipArt.Decode(png)!)
            Assert.AreEqual(new SKRectI(0, 47, 480, 313), ChipArt.ContentBounds(bitmap)); // bars plus a 2 px JPEG-fringe margin

        using var output = Decode(ChipImageBuilder.RenderFront(png, "", null, NoTitle).Png);
        AssertClose(SKColors.Orange, output.GetPixel(239, 34), where: "window top");
        AssertClose(SKColors.Orange, output.GetPixel(239, 443), where: "window bottom");
    }

    [TestMethod]
    public void A_dark_image_without_matching_bars_is_not_trimmed()
    {
        var png = Png(400, 400, c =>
        {
            c.Clear(SKColors.Black);
            using var white = new SKPaint { Color = SKColors.White };
            c.DrawRect(SKRect.Create(0, 300, 400, 100), white); // dark top, light bottom: no letterbox
        });
        using var bitmap = ChipArt.Decode(png)!;
        Assert.AreEqual(new SKRectI(0, 0, 400, 400), ChipArt.ContentBounds(bitmap));
    }

    [TestMethod]
    public void Scale_two_renders_956_and_other_scales_are_rejected()
    {
        var chip = ChipImageBuilder.RenderFront(Solid(64, 64, SKColors.Blue), "Title", ServiceKind.YouTube, new ChipOptions { Scale = 2 });
        Assert.AreEqual((956, 956), (chip.Width, chip.Height));
        using var output = Decode(chip.Png);
        Assert.AreEqual((956, 956), (output.Width, output.Height));
        AssertClose(SKColors.Blue, output.GetPixel(478, 300));
        Assert.AreEqual(0, output.GetPixel(0, 0).Alpha);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ChipImageBuilder.RenderFront(null, "x", null, new ChipOptions { Scale = 3 }));
    }

    [TestMethod]
    public void Generated_background_differs_per_service_and_is_opaque()
    {
        var hashes = new[] { ServiceKind.Spotify, ServiceKind.YouTube, ServiceKind.LastFm }
            .Select(s => ChipImageBuilder.RenderFront(null, "", s, NoTitle))
            .Append(ChipImageBuilder.RenderFront(null, "", null, NoTitle))
            .Select(chip =>
            {
                Assert.AreEqual(ChipArtSource.Generated, chip.ArtSource);
                using var output = Decode(chip.Png);
                Assert.AreEqual(255, output.GetPixel(239, 239).Alpha);
                Assert.AreEqual(255, output.GetPixel(100, 440).Alpha);
                return chip.Hash;
            })
            .ToList();
        Assert.AreEqual(hashes.Count, hashes.Distinct().Count());
    }

    [TestMethod]
    public void Undecodable_art_falls_back_to_the_generated_background()
    {
        var garbage = ChipImageBuilder.RenderFront([1, 2, 3, 4], "T", ServiceKind.Spotify);
        var none = ChipImageBuilder.RenderFront(null, "T", ServiceKind.Spotify);
        Assert.AreEqual(ChipArtSource.Generated, garbage.ArtSource);
        Assert.AreEqual(none.Hash, garbage.Hash);
    }

    [TestMethod]
    public void Hiding_the_title_matches_an_empty_title()
    {
        var art = Solid(32, 32, SKColors.Teal);
        var hidden = ChipImageBuilder.RenderFront(art, "Something", null, NoTitle);
        Assert.AreEqual(ChipImageBuilder.RenderFront(art, "   ", null).Hash, hidden.Hash);
        Assert.AreNotEqual(ChipImageBuilder.RenderFront(art, "Something", null).Hash, hidden.Hash);
    }

    [TestMethod]
    public void Back_image_is_the_original_chip_back()
    {
        var back = ChipImageBuilder.BackImage;
        Assert.AreSame(back, ChipImageBuilder.BackImage);
        Assert.AreSequenceEqual(ChipAssets.Back, back.Png);
        Assert.AreEqual((477, 478), (back.Width, back.Height));
        Assert.AreEqual(ChipArtSource.None, back.ArtSource);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(back.Png)), back.Hash);
    }

    [TestMethod]
    public async Task Builds_from_the_collection_thumbnail()
    {
        var http = new FakeHttp();
        var art = Solid(300, 300, SKColors.Blue);
        Uri? requested = null;
        http.Add(r => { requested = r.RequestUri; return Task.FromResult(PngResponse(art)); });
        var chip = await new ChipImageBuilder(http.Client()).BuildFrontAsync(Collection("https://img.example/cover.png"), new ChipOptions());

        Assert.AreEqual(new Uri("https://img.example/cover.png"), requested);
        Assert.AreEqual(ChipArtSource.Collection, chip.ArtSource);
        Assert.AreEqual(ChipImageBuilder.RenderFront(art, "Woody's Fine EDM", ServiceKind.Spotify).Hash, chip.Hash);
    }

    [TestMethod]
    public async Task Falls_back_to_the_first_track_with_art()
    {
        var http = new FakeHttp();
        http.Add("", status: HttpStatusCode.NotFound);
        http.Add(_ => Task.FromResult(PngResponse(Solid(64, 64, SKColors.Red))));
        var chip = await new ChipImageBuilder(http.Client())
            .BuildFrontAsync(Collection("https://img.example/missing.png", "https://img.example/track.png"), NoTitle);

        Assert.AreEqual(ChipArtSource.Track, chip.ArtSource);
        Assert.AreEqual(2, http.Requests);
        using var output = Decode(chip.Png);
        AssertClose(SKColors.Red, output.GetPixel(239, 239));
    }

    [TestMethod]
    public async Task Download_failures_fall_back_to_the_generated_background()
    {
        var http = new FakeHttp();
        http.Add(_ => throw new HttpRequestException("offline"));
        http.Add(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not an image</html>") }));
        var options = new ChipOptions { Title = "Override" };
        var chip = await new ChipImageBuilder(http.Client())
            .BuildFrontAsync(Collection("https://img.example/a.png", "https://img.example/b.png", service: ServiceKind.YouTube), options);

        Assert.AreEqual(ChipArtSource.Generated, chip.ArtSource);
        Assert.AreEqual(2, http.Requests);
        Assert.AreEqual(ChipImageBuilder.RenderFront(null, "Override", ServiceKind.YouTube).Hash, chip.Hash);
    }

    [TestMethod]
    public async Task A_timeout_is_a_failure_not_a_cancellation()
    {
        var http = new FakeHttp();
        http.Add(_ => throw new TaskCanceledException("timed out"));
        var chip = await new ChipImageBuilder(http.Client()).BuildFrontAsync(Collection("https://img.example/slow.png"), NoTitle);
        Assert.AreEqual(ChipArtSource.Generated, chip.ArtSource);
    }

    [TestMethod]
    public async Task Missing_or_non_http_urls_are_not_requested()
    {
        var http = new FakeHttp();
        var chip = await new ChipImageBuilder(http.Client())
            .BuildFrontAsync(Collection("file:///C:/secret.png", "not a url"), NoTitle);
        Assert.AreEqual(0, http.Requests);
        Assert.AreEqual(ChipArtSource.Generated, chip.ArtSource);
    }

    [TestMethod]
    public async Task Oversized_art_is_not_downloaded()
    {
        var http = new FakeHttp();
        http.Add(_ =>
        {
            var content = new ByteArrayContent([0]);
            content.Headers.ContentLength = ChipImageBuilder.MaxArtBytes + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        var chip = await new ChipImageBuilder(http.Client()).BuildFrontAsync(Collection("https://img.example/huge.png"), NoTitle);
        Assert.AreEqual(ChipArtSource.Generated, chip.ArtSource);
    }

    [TestMethod]
    public async Task Cancellation_before_start_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var http = new FakeHttp();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ChipImageBuilder(http.Client()).BuildFrontAsync(Collection("https://img.example/a.png"), NoTitle, cts.Token));
        Assert.AreEqual(0, http.Requests);
    }

    [TestMethod]
    public async Task Cancellation_during_download_throws_instead_of_falling_back()
    {
        using var cts = new CancellationTokenSource();
        var http = new FakeHttp();
        http.Add(_ =>
        {
            cts.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cts.Token);
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ChipImageBuilder(http.Client()).BuildFrontAsync(Collection("https://img.example/a.png", "https://img.example/b.png"), NoTitle, cts.Token));
        Assert.AreEqual(1, http.Requests);
    }
}
