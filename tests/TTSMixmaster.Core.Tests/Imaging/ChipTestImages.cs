using SkiaSharp;
using TTSMixmaster.Core.Imaging;

namespace TTSMixmaster.Core.Tests.Imaging;

internal static class ChipTestImages
{
    /// <summary>Encodes a PNG drawn by <paramref name="draw"/>.</summary>
    internal static byte[] Png(int width, int height, Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        draw(surface.Canvas);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static byte[] Solid(int width, int height, SKColor color) => Png(width, height, c => c.Clear(color));

    /// <summary>Green with a centred red square as tall/wide as the image's short side.</summary>
    internal static byte[] CentreSquare(int width, int height) => Png(width, height, c =>
    {
        c.Clear(SKColors.Lime);
        var side = Math.Min(width, height);
        using var red = new SKPaint { Color = SKColors.Red };
        c.DrawRect(SKRect.Create((width - side) / 2f, (height - side) / 2f, side, side), red);
    });

    internal static SKBitmap Decode(byte[] png) => ChipFrame.Decode(png);

    internal static void AssertClose(SKColor expected, SKColor actual, int tolerance = 8, string? where = null)
    {
        var ok = Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance &&
                 Math.Abs(expected.Blue - actual.Blue) <= tolerance && Math.Abs(expected.Alpha - actual.Alpha) <= tolerance;
        Assert.True(ok, $"{where}: expected ~{expected}, got {actual}");
    }
}
