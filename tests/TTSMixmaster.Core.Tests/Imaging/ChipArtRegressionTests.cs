using System.Diagnostics;
using System.Net;
using SkiaSharp;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;
using static TTSMixmaster.Core.Tests.Imaging.ChipTestImages;

namespace TTSMixmaster.Core.Tests.Imaging;

/// <summary>
/// Review findings on the mosaic (PR #36): one deadline for the whole fetch, no second bar detection on a composed
/// mosaic, and no fringe crop of bright lossless borders.
/// </summary>
[TestClass]
public sealed class ChipArtRegressionTests
{
    private static readonly ChipOptions NoTitle = new() { ShowTitle = false };
    private static readonly SKColor[] Colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];

    /// <summary>Serves <c>https://img.example/{i}.png</c> from <paramref name="art"/>; null hangs until cancelled.</summary>
    private sealed class HangingServer(Func<int, byte[]?> art) : HttpMessageHandler
    {
        private int _active;
        public int Requests;
        public int Peak;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var active = Interlocked.Increment(ref _active);
            InterlockedMax(ref Peak, active);
            try
            {
                var index = int.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath), System.Globalization.CultureInfo.InvariantCulture);
                var bytes = index >= 0 ? art(index) : null;
                if (bytes is null) await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes!) };
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    private static MusicCollection Collection(int covers, string thumbnail = "") => new()
    {
        Summary = new() { Id = "c", Name = "Mix", Service = ServiceKind.YouTube, Kind = CollectionKind.Playlist, ThumbnailUrl = thumbnail },
        Tracks = [.. Enumerable.Range(0, covers).Select(i => new Track { Title = $"T{i}", Artist = "A", ThumbnailUrl = $"https://img.example/{i}.png" })],
    };

    [TestMethod]
    public async Task Stalled_replacement_covers_share_one_deadline_and_what_loaded_is_used()
    {
        // Three covers load at once; the other nine stall. Per-request timeouts let nine replacements wait in turn.
        var timeout = TimeSpan.FromMilliseconds(300);
        var server = new HangingServer(i => i < 3 ? Solid(64, 64, Colors[i]) : null);
        var clock = Stopwatch.StartNew();

        var fetch = await new ChipImageBuilder(new HttpClient(server), timeout).FetchArtAsync(Collection(12));

        Assert.IsLessThan(timeout * 3, clock.Elapsed, "one deadline, not one per replacement");
        Assert.AreEqual(ChipArtSource.Track, fetch.Source, "fewer than four covers loaded: the first alone");
        AssertClose(Colors[0], Decode(fetch.Art!).GetPixel(10, 10));
        Assert.IsFalse(fetch.TimedOut, "art was found");
        Assert.AreEqual(4, server.Requests, "no replacement starts after the deadline");
        Assert.IsLessThanOrEqualTo(4, server.Peak);
    }

    [TestMethod]
    public async Task The_deadline_covers_own_art_too_and_ends_in_the_generated_background()
    {
        var server = new HangingServer(i => Solid(64, 64, Colors[i % 4]));
        var clock = Stopwatch.StartNew();

        var fetch = await new ChipImageBuilder(new HttpClient(server), TimeSpan.FromMilliseconds(300))
            .FetchArtAsync(Collection(4, "https://img.example/-1.png"));

        Assert.IsLessThan(TimeSpan.FromSeconds(1), clock.Elapsed);
        Assert.IsNull(fetch.Art);
        Assert.IsTrue(fetch.TimedOut);
        Assert.AreEqual(1, server.Requests, "no cover is started once the fetch's deadline has passed");
    }

    [TestMethod]
    public async Task Caller_cancellation_still_throws_and_leaves_no_request_running()
    {
        var server = new HangingServer(_ => null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new ChipImageBuilder(new HttpClient(server)).FetchArtAsync(Collection(12), cts.Token));
        Assert.AreEqual(4, server.Requests);
    }

    /// <summary>A square cover with dark artwork along one edge: the top of covers 0–1, the bottom of covers 2–3.</summary>
    private static byte[] HalfDark(int index) => Png(512, 512, c =>
    {
        c.Clear(Colors[index]);
        using var black = new SKPaint { Color = SKColors.Black };
        c.DrawRect(0, index < 2 ? 0 : 352, 512, 160, black);
    });

    [TestMethod]
    public async Task A_composed_mosaic_is_not_trimmed_again_when_rendered()
    {
        // Each cover keeps its dark edge; composed, the dark top and bottom rows line up like a letterbox.
        var server = new HangingServer(i => i < 4 ? HalfDark(i) : null);
        var builder = new ChipImageBuilder(new HttpClient(server));

        var fetch = await builder.FetchArtAsync(Collection(4));
        Assert.AreEqual(ChipArtSource.Mosaic, fetch.Source);
        Assert.IsTrue(fetch.IsPrepared);

        // The App's cached-art path: RenderFront with the prepared flag.
        using var cached = Decode(ChipImageBuilder.RenderFront(fetch.Art, "", ServiceKind.YouTube, NoTitle, fetch.IsPrepared).Png);
        // BuildFrontAsync carries the flag itself.
        using var built = Decode((await builder.BuildFrontAsync(Collection(4), NoTitle)).Png);
        foreach (var output in new[] { cached, built })
        {
            AssertClose(SKColors.Black, output.GetPixel(180, 60), where: "dark artwork along the top of cover 0");
            AssertClose(Colors[0], output.GetPixel(180, 200), where: "cover 0 below its dark strip");
            AssertClose(SKColors.Black, output.GetPixel(300, 420), where: "dark artwork along the bottom of cover 3");
        }
    }

    [TestMethod]
    public void Plain_art_is_still_trimmed_when_rendered()
    {
        Assert.IsFalse(new ChipArtFetch([1], false, ChipArtSource.Track).IsPrepared);
        Assert.IsFalse(new ChipArtFetch([1], false, ChipArtSource.Collection).IsPrepared);
    }

    [TestMethod]
    public void A_bright_border_beside_a_thin_black_frame_survives_trimming()
    {
        // Lossless art: a 1 px black frame, a 2 px red band, a green interior. Only the black frame is a bar.
        using var art = new SKBitmap(64, 64);
        art.Erase(SKColors.Black);
        using (var canvas = new SKCanvas(art))
        {
            using var red = new SKPaint { Color = SKColors.Red };
            using var green = new SKPaint { Color = SKColors.Lime };
            canvas.DrawRect(1, 1, 62, 62, red);
            canvas.DrawRect(3, 3, 58, 58, green);
        }
        Assert.AreEqual(new SKRectI(1, 1, 63, 63), ChipArt.ContentBounds(art));

        using var png = art.Encode(SKEncodedImageFormat.Png, 100);
        using var output = Decode(ChipImageBuilder.RenderFront(png.ToArray(), "", null, NoTitle).Png);
        AssertClose(SKColors.Red, output.GetPixel(36, 239), tolerance: 40, where: "the red band at the window's left edge");
    }

    [TestMethod]
    public void A_jpeg_ringing_fringe_beside_trimmed_bars_is_dropped()
    {
        // 4:3 thumbnail of a 16:9 video with square art: JPEG leaves a dark-red line at the bars' edges.
        var jpeg = Encode(Png(480, 360, c =>
        {
            c.Clear(SKColors.Black);
            using var orange = new SKPaint { Color = new SKColor(0xF0, 0x95, 0x5C) };
            c.DrawRect(SKRect.Create(105, 45, 270, 270), orange);
        }), SKEncodedImageFormat.Jpeg);
        using var art = ChipArt.Decode(jpeg)!;

        var bounds = ChipArt.ContentBounds(art);

        foreach (var (x, y) in new[] { (bounds.Left, 180), (bounds.Right - 1, 180), (240, bounds.Top), (240, bounds.Bottom - 1) })
        {
            var c = art.GetPixel(x, y);
            Assert.IsGreaterThan(150, Math.Max(c.Red, Math.Max(c.Green, c.Blue)), $"edge pixel ({x},{y}) of {bounds} is picture, not fringe: {c}");
        }
        Assert.IsLessThanOrEqualTo(4, bounds.Left - 105 + (375 - bounds.Right), "no more than the fringe is dropped");
    }

    private static byte[] Encode(byte[] png, SKEncodedImageFormat format)
    {
        using var bitmap = SKBitmap.Decode(png);
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }
}
