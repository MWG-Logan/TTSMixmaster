using SkiaSharp;

namespace TTSMixmaster.Core.Imaging;

/// <summary>
/// Derives Assets/Chip/frame.png from Assets/Chip/front.png: the art window (see <see cref="ChipGeometry"/>)
/// becomes transparent, every other pixel is kept byte for byte. Run scripts/derive-chip-frame.cs to regenerate.
/// </summary>
public static class ChipFrame
{
    /// <summary>The slate the frame is painted in, used only if an edge pixel has no plain-frame neighbour.</summary>
    private static readonly SKColor FrameSlate = new(0x26, 0x26, 0x32);

    /// <summary>Decodes a PNG as unpremultiplied RGBA so untouched pixels round-trip exactly.</summary>
    public static SKBitmap Decode(byte[] png)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(png)) ?? throw new ArgumentException("Not a decodable image.", nameof(png));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new ArgumentException($"Could not decode image: {result}.", nameof(png));
        }
        return bitmap;
    }

    /// <summary>Cuts the window out of the chip front.</summary>
    /// <remarks>
    /// Pixels fully inside the window become transparent; pixels fully outside are copied unchanged.
    /// Edge pixels get alpha = 1 - (exact window coverage) and the colour of their plain-frame neighbours,
    /// not their own colour: the source edge pixels are frame blended with the black panel, and keeping
    /// that black would leave a dark fringe around the art.
    /// </remarks>
    public static SKBitmap Derive(SKBitmap front)
    {
        ArgumentNullException.ThrowIfNull(front);
        if (front.Width != ChipGeometry.Size || front.Height != ChipGeometry.Size)
            throw new ArgumentException($"The chip front must be {ChipGeometry.Size}x{ChipGeometry.Size}.", nameof(front));

        const int size = ChipGeometry.Size;
        var src = new SKColor[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                src[y * size + x] = front.GetPixel(x, y); // unpremultiplied colour

        var coverage = new double[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                coverage[y * size + x] = ChipGeometry.Coverage(x, y);

        var frame = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var c = coverage[y * size + x];
                var pixel = src[y * size + x];
                if (c <= 0) frame.SetPixel(x, y, pixel);
                else if (c >= 1) frame.SetPixel(x, y, SKColors.Transparent);
                else
                {
                    var color = NeighbourFrameColour(src, coverage, x, y);
                    var alpha = (byte)Math.Round(pixel.Alpha * (1 - c), MidpointRounding.AwayFromZero);
                    frame.SetPixel(x, y, color.WithAlpha(alpha));
                }
            }
        }
        return frame;
    }

    /// <summary>Encodes a bitmap as PNG.</summary>
    public static byte[] Encode(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("PNG encoding failed.");
        return data.ToArray();
    }

    private static SKColor NeighbourFrameColour(SKColor[] src, double[] coverage, int x, int y)
    {
        const int size = ChipGeometry.Size;
        int r = 0, g = 0, b = 0, n = 0;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                var i = ny * size + nx;
                if (coverage[i] > 0 || src[i].Alpha != 255) continue;
                r += src[i].Red; g += src[i].Green; b += src[i].Blue; n++;
            }
        }
        return n == 0 ? FrameSlate : new SKColor((byte)((r + n / 2) / n), (byte)((g + n / 2) / n), (byte)((b + n / 2) / n));
    }
}
