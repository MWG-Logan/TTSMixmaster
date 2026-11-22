using System.Net;
using SkiaSharp;
using TTSMixmaster.Core.Imaging;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Tests.Services;
using static TTSMixmaster.Core.Tests.Imaging.ChipTestImages;

namespace TTSMixmaster.Core.Tests.Imaging;

/// <summary>Art is untrusted internet input: only PNG, JPEG and WebP reach Skia's decoders, and only over https.</summary>
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

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void Png_jpeg_and_webp_are_decoded(SKEncodedImageFormat format)
    {
        using var bitmap = ChipArt.Decode(Encode(format));
        Assert.NotNull(bitmap);
        Assert.Equal(ChipArtSource.Collection, ChipImageBuilder.RenderFront(Encode(format), "T", null).ArtSource);
    }

    [Fact]
    public void Other_formats_skia_could_decode_are_rejected()
    {
        // Sanity: Skia itself would decode both, so the rejection is ours.
        using (var gif = SKCodec.Create(new SKMemoryStream(Gif))) Assert.Equal(SKEncodedImageFormat.Gif, gif?.EncodedFormat);
        using (var bmp = SKCodec.Create(new SKMemoryStream(Bmp))) Assert.Equal(SKEncodedImageFormat.Bmp, bmp?.EncodedFormat);

        Assert.Null(ChipArt.Decode(Gif));
        Assert.Null(ChipArt.Decode(Bmp));
        Assert.Equal(ChipArtSource.Generated, ChipImageBuilder.RenderFront(Gif, "T", null).ArtSource);
        Assert.Equal(ChipArtSource.Generated, ChipImageBuilder.RenderFront(Bmp, "T", null).ArtSource);
    }

    [Fact]
    public void A_webp_signature_needs_the_full_riff_header()
    {
        Assert.False(ChipArt.HasAllowedSignature("RIFF"u8));
        Assert.False(ChipArt.HasAllowedSignature("RIFF\0\0\0\0WAVE"u8));
        Assert.True(ChipArt.HasAllowedSignature("RIFF\0\0\0\0WEBP"u8));
    }

    [Theory]
    [InlineData("http://img.example/cover.png", "https://img.example/cover.png")]
    [InlineData("http://img.example:8080/a.png?x=1", "https://img.example:8080/a.png?x=1")]
    [InlineData("https://img.example/cover.png", "https://img.example/cover.png")]
    [InlineData("ftp://img.example/cover.png", null)]
    [InlineData("", null)]
    public void Art_urls_are_https_only(string url, string? expected) =>
        Assert.Equal(expected, ChipImageBuilder.ArtUri(url)?.AbsoluteUri);

    [Fact]
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

        Assert.Equal(new Uri("https://img.example/c.png"), requested);
        Assert.Equal(ChipArtSource.Collection, chip.ArtSource);
    }

    [Fact]
    public void Thumbnail_is_a_256_png_of_the_chip()
    {
        var chip = ChipImageBuilder.RenderFront(Solid(32, 32, SKColors.Blue), "T", null);
        var thumbnail = ChipImageBuilder.Thumbnail(chip);
        using var decoded = Decode(thumbnail);
        Assert.Equal((256, 256), (decoded.Width, decoded.Height));
        AssertClose(SKColors.Blue, decoded.GetPixel(128, 100));
    }
}
