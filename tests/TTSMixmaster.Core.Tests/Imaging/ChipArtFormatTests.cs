using System.Net;
using SkiaSharp;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tests.Services;
using static TTSMixmaster.Core.Tests.Imaging.ChipTestImages;

namespace TTSMixmaster.Core.Tests.Imaging;

/// <summary>Art is untrusted internet input: only PNG, JPEG and WebP reach Skia's decoders, and only over https.</summary>
[TestClass]
public sealed class ChipArtFormatTests
{
    private static byte[] Encode(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    // A 1×1 GIF89a and a 1×1 24-bit BMP: formats Skia can decode but the app must not.
    private static readonly byte[] Gif = Convert.FromHexString(
        "47494638396101000100800000000000FFFFFF21F90401000000002C00000000010001000002024401003B");

    private static readonly byte[] Bmp = Convert.FromHexString(
        "424D3A000000000000003600000028000000010000000100000001001800000000000400000000000000000000000000000000000000" +
        "FF000000");

    [TestMethod]
    [DataRow(SKEncodedImageFormat.Png)]
    [DataRow(SKEncodedImageFormat.Jpeg)]
    [DataRow(SKEncodedImageFormat.Webp)]
    public void Png_jpeg_and_webp_are_decoded(SKEncodedImageFormat format)
    {
        using var bitmap = ChipArt.Decode(Encode(format));
        Assert.IsNotNull(bitmap);
        Assert.AreEqual(ChipArtSource.Collection, ChipImageBuilder.RenderFront(Encode(format), "T", null).ArtSource);
    }

    [TestMethod]
    public void Other_formats_skia_could_decode_are_rejected()
    {
        // Sanity: Skia itself would decode both, so the rejection is ours.
        using (var gif = SKCodec.Create(new SKMemoryStream(Gif))) Assert.AreEqual(SKEncodedImageFormat.Gif, gif?.EncodedFormat);
        using (var bmp = SKCodec.Create(new SKMemoryStream(Bmp))) Assert.AreEqual(SKEncodedImageFormat.Bmp, bmp?.EncodedFormat);

        Assert.IsNull(ChipArt.Decode(Gif));
        Assert.IsNull(ChipArt.Decode(Bmp));
        Assert.AreEqual(ChipArtSource.Generated, ChipImageBuilder.RenderFront(Gif, "T", null).ArtSource);
        Assert.AreEqual(ChipArtSource.Generated, ChipImageBuilder.RenderFront(Bmp, "T", null).ArtSource);
    }

    [TestMethod]
    public void A_webp_signature_needs_the_full_riff_header()
    {
        Assert.IsFalse(ChipArt.HasAllowedSignature("RIFF"u8));
        Assert.IsFalse(ChipArt.HasAllowedSignature("RIFF\0\0\0\0WAVE"u8));
        Assert.IsTrue(ChipArt.HasAllowedSignature("RIFF\0\0\0\0WEBP"u8));
    }

    [TestMethod]
    [DataRow("http://img.example/cover.png", "https://img.example/cover.png")]
    [DataRow("http://img.example:8080/a.png?x=1", "https://img.example:8080/a.png?x=1")]
    [DataRow("https://img.example/cover.png", "https://img.example/cover.png")]
    [DataRow("ftp://img.example/cover.png", null)]
    [DataRow("", null)]
    public void Art_urls_are_https_only(string url, string? expected) =>
        Assert.AreEqual(expected, ChipImageBuilder.ArtUri(url)?.AbsoluteUri);

    [TestMethod]
    public async Task Plain_http_art_is_fetched_over_https()
    {
        var http = new FakeHttp();
        Uri? requested = null;
        http.Add(r =>
        {
            requested = r.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Solid(16, 16, SKColors.Red)) });
        });
        var collection = new MusicCollection
        {
            Summary = new CollectionSummary { Id = "p", Name = "P", Service = ServiceKind.LastFm, Kind = CollectionKind.Chart, ThumbnailUrl = "http://img.example/c.png" },
            Tracks = [],
        };

        var chip = await new ChipImageBuilder(http.Client()).BuildFrontAsync(collection, new ChipOptions());

        Assert.AreEqual(new Uri("https://img.example/c.png"), requested);
        Assert.AreEqual(ChipArtSource.Collection, chip.ArtSource);
    }

    [TestMethod]
    public void Thumbnail_is_a_256_png_of_the_chip()
    {
        var chip = ChipImageBuilder.RenderFront(Solid(32, 32, SKColors.Blue), "T", null);
        var thumbnail = ChipImageBuilder.Thumbnail(chip);
        using var decoded = Decode(thumbnail);
        Assert.AreEqual((256, 256), (decoded.Width, decoded.Height));
        AssertClose(SKColors.Blue, decoded.GetPixel(128, 100));
    }
}
