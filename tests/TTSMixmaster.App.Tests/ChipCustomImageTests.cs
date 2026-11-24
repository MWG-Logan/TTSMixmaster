using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;
using SkiaSharp;
using TTSMixmaster.App.Tests.Infrastructure;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.Tests;

/// <summary>"Use custom image…" wins over the collection's art, including a track-cover mosaic.</summary>
[TestClass]
public sealed class ChipCustomImageTests
{
    [UITestMethod]
    public async Task A_custom_image_replaces_the_mosaic_and_no_cover_is_downloaded()
    {
        using var app = new TestApp();
        var playlist = TestApp.Collection();
        // A YouTube playlist with only YouTube's stand-in thumbnail: without the custom image it would be a mosaic.
        var collection = playlist with
        {
            Summary = playlist.Summary with { ThumbnailUrl = "https://i.ytimg.com/vi/v0/maxresdefault.jpg", ArtKind = CollectionArtKind.ServiceDefault },
            Tracks = [.. playlist.Tracks.Select((t, i) => t with { ThumbnailUrl = $"https://i.ytimg.com/vi/v{i}/hqdefault.jpg" })],
        };
        var custom = Path.Combine(app.Root, "custom.png");
        Directory.CreateDirectory(app.Root);
        await File.WriteAllBytesAsync(custom, Solid(SKColors.Magenta));
        app.Settings.ChipCustomImages[AppSettings.ChipImageKey(collection.Summary)] = custom;

        var vm = app.OpenCollection(collection);
        await vm.ActivateAsync();
        await Wait.UntilAsync(() => vm.ChipPng is not null, "the chip preview", timeoutMs: 10000);

        Assert.IsTrue(vm.HasCustomChipImage);
        using var chip = SKBitmap.Decode(vm.ChipPng);
        var centre = chip.GetPixel(chip.Width / 2, chip.Height / 4);
        Assert.IsTrue(centre.Red > 200 && centre.Green < 60 && centre.Blue > 200, $"expected the magenta custom image, got {centre}");
        Assert.IsEmpty(app.Network.Requests, "no art is downloaded when a custom image is chosen");
    }

    private static byte[] Solid(SKColor color)
    {
        using var bitmap = new SKBitmap(64, 64);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
