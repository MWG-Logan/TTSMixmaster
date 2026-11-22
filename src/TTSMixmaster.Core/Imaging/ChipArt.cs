using SkiaSharp;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Imaging;

/// <summary>Decodes, trims and cover-fits the art behind the chip frame, or paints a generated background.</summary>
internal static class ChipArt
{
    /// <summary>Art is kept at most this large on its longest side: the window is only ~415px.</summary>
    internal const int MaxDecodeSize = 2048;

    /// <summary>
    /// The most pixels a source may have (4096 × 4096, 64 MiB as RGBA). Compressed size says nothing about
    /// decoded size (a uniform 16384² PNG is a few kilobytes), so the header's dimensions are checked against
    /// this before anything is allocated, and nothing larger is ever decoded.
    /// </summary>
    internal const long MaxSourcePixels = 4096L * 4096;

    /// <summary>
    /// Decodes art, returning null for anything that isn't a usable PNG, JPEG or WebP image within
    /// <see cref="MaxSourcePixels"/>. Art is untrusted internet input, so every other format Skia could decode
    /// (GIF, BMP, ICO, WBMP, HEIF, AVIF, …) is rejected by its signature before any codec sees it, and again by
    /// the codec's detected format before any pixels are decoded.
    /// </summary>
    internal static SKBitmap? Decode(byte[]? bytes)
    {
        if (bytes is null || !HasAllowedSignature(bytes)) return null;
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp)) return null;
        if ((long)codec.Info.Width * codec.Info.Height > MaxSourcePixels) return null;

        // JPEG and WebP codecs can decode straight to a smaller size; PNG's returns the full size, which the
        // budget check above has already bounded, and is resized after decoding.
        var size = codec.Info.Size;
        var longest = Math.Max(size.Width, size.Height);
        if (longest > MaxDecodeSize) size = codec.GetScaledDimensions((float)MaxDecodeSize / longest);
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > MaxSourcePixels) return null;

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            return null;
        }
        return Fit(bitmap);
    }

    /// <summary>Downscales a decoded bitmap whose longest side is over <see cref="MaxDecodeSize"/> (and disposes the original).</summary>
    private static SKBitmap? Fit(SKBitmap bitmap)
    {
        var longest = Math.Max(bitmap.Width, bitmap.Height);
        if (longest <= MaxDecodeSize) return bitmap;
        var scale = (double)MaxDecodeSize / longest;
        var info = new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        using (bitmap)
            return bitmap.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    /// <summary>PNG (89 50 4E 47 0D 0A 1A 0A), JPEG (FF D8 FF) or WebP (RIFF ???? WEBP) magic bytes.</summary>
    internal static bool HasAllowedSignature(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ||
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ||
        (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8));

    /// <summary>
    /// The part of the image worth showing: drops symmetric black letterbox/pillarbox bars, as on
    /// YouTube's 4:3 thumbnails of 16:9 videos, so cover-fit crops into the picture rather than the bars.
    /// </summary>
    internal static SKRectI ContentBounds(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var top = CountBars(h, i => IsDarkLine(bitmap, i, horizontal: true, BarLevel));
        var bottom = CountBars(h, i => IsDarkLine(bitmap, h - 1 - i, horizontal: true, BarLevel));
        var left = CountBars(w, i => IsDarkLine(bitmap, i, horizontal: false, BarLevel));
        var right = CountBars(w, i => IsDarkLine(bitmap, w - 1 - i, horizontal: false, BarLevel));

        // Only trim matched pairs of bars that leave a substantial picture; a dark sky is not a letterbox.
        var trimRows = top > 0 && bottom > 0 && Math.Abs(top - bottom) <= h / 50 + 2 && h - top - bottom >= h * 2 / 5;
        var trimCols = left > 0 && right > 0 && Math.Abs(left - right) <= w / 50 + 2 && w - left - right >= w * 2 / 5;
        if (trimRows)
        {
            top += Fringe(h, top, i => IsDarkLine(bitmap, i, horizontal: true, FringeLevel));
            bottom += Fringe(h, bottom, i => IsDarkLine(bitmap, h - 1 - i, horizontal: true, FringeLevel));
        }
        if (trimCols)
        {
            left += Fringe(w, left, i => IsDarkLine(bitmap, i, horizontal: false, FringeLevel));
            right += Fringe(w, right, i => IsDarkLine(bitmap, w - 1 - i, horizontal: false, FringeLevel));
        }
        return new SKRectI(trimCols ? left : 0, trimRows ? top : 0, trimCols ? w - right : w, trimRows ? h - bottom : h);
    }

    /// <summary>Brightest channel a JPEG-ringing fringe line may have (a bar line may have <see cref="BarLevel"/>).</summary>
    private const int FringeLevel = 64;
    private const int BarLevel = 24;

    /// <summary>
    /// JPEG ringing leaves a dark line or two where a bar meets the picture, just too bright to count as bar. Beyond a
    /// trimmed bar of <paramref name="bar"/> lines, this many more lines are such near-dark fringe: at most 2 px (or
    /// ~0.4% of the side on large art), and only lines that really are near-dark, so a bright border survives.
    /// </summary>
    private static int Fringe(int length, int bar, Func<int, bool> isFringe)
    {
        var limit = Math.Max(2, length / 256);
        var count = 0;
        while (count < limit && bar + count < length / 2 && isFringe(bar + count)) count++;
        return count;
    }

    /// <summary>The centred crop of <paramref name="source"/> with the aspect ratio of <paramref name="target"/>.</summary>
    internal static SKRect CoverCrop(SKRect source, SKRect target)
    {
        var targetAspect = target.Width / target.Height;
        var sourceAspect = source.Width / source.Height;
        if (sourceAspect > targetAspect)
        {
            var width = source.Height * targetAspect;
            var x = source.Left + (source.Width - width) / 2;
            return new SKRect(x, source.Top, x + width, source.Bottom);
        }
        var height = source.Width / targetAspect;
        var y = source.Top + (source.Height - height) / 2;
        return new SKRect(source.Left, y, source.Right, y + height);
    }

    /// <summary>
    /// Draws the art cover-fitted (centre-cropped) into <paramref name="target"/>, first trimming letterbox bars unless
    /// <paramref name="trimBars"/> is false (art that is already trimmed and fitted, such as a mosaic).
    /// </summary>
    internal static void DrawCover(SKCanvas canvas, SKBitmap art, SKRect target, bool trimBars = true)
    {
        var content = trimBars ? ContentBounds(art) : SKRectI.Create(art.Width, art.Height);
        using var whole = SKImage.FromBitmap(art);
        // Trimmed bars are cut off as a subset, so filtering and mipmaps at the crop's edge can't blend them back in
        // (a dark seam between mosaic tiles).
        var trimmed = content != SKRectI.Create(art.Width, art.Height);
        using var subset = trimmed ? whole.Subset(content) : null;
        var image = subset ?? whole;
        var crop = CoverCrop(SKRect.Create(image.Width, image.Height), target);
        var scale = canvas.TotalMatrix.ScaleX * target.Width / crop.Width;
        // Upscaling small art: a cubic keeps it soft rather than blocky. Downscaling: mipmaps avoid aliasing.
        var sampling = scale > 1
            ? new SKSamplingOptions(SKCubicResampler.Mitchell)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, crop, target, sampling, paint);
    }

    /// <summary>A dark backdrop tinted for the service, with faint record grooves; never the MWG crest.</summary>
    internal static void DrawGenerated(SKCanvas canvas, SKRect target, ServiceKind? service)
    {
        var (light, dark, accent) = service switch
        {
            ServiceKind.Spotify => (new SKColor(0x1A, 0x5E, 0x3A), new SKColor(0x05, 0x14, 0x0C), new SKColor(0x1D, 0xB9, 0x54)),
            ServiceKind.YouTube => (new SKColor(0x6E, 0x16, 0x16), new SKColor(0x16, 0x05, 0x05), new SKColor(0xFF, 0x33, 0x33)),
            ServiceKind.LastFm => (new SKColor(0x5E, 0x10, 0x2C), new SKColor(0x14, 0x04, 0x0B), new SKColor(0xD5, 0x10, 0x4A)),
            _ => (new SKColor(0x34, 0x38, 0x52), new SKColor(0x0D, 0x0E, 0x16), new SKColor(0x8A, 0x92, 0xC8)),
        };

        using (var shader = SKShader.CreateLinearGradient(
                   new SKPoint(target.Left, target.Top), new SKPoint(target.Right, target.Bottom),
                   [light, dark], [0, 1], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
            canvas.DrawRect(target, paint);

        // Soft glow behind the record, upper-centre.
        var cx = target.MidX;
        var cy = target.Top + target.Height * 0.42f;
        using (var glow = SKShader.CreateRadialGradient(new SKPoint(cx, cy), target.Width * 0.55f,
                   [accent.WithAlpha(0x38), accent.WithAlpha(0)], [0, 1], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = glow, IsAntialias = true })
            canvas.DrawRect(target, paint);

        // A record: faint grooves, a slightly brighter label, and a spindle hole.
        var outer = target.Width * 0.36f;
        using (var groove = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White.WithAlpha(0x0E) })
        {
            for (var r = outer * 0.38f; r <= outer; r += 5.5f)
                canvas.DrawCircle(cx, cy, r, groove);
        }
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.White.WithAlpha(0x1C) })
            canvas.DrawCircle(cx, cy, outer, rim);
        using (var label = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(0x40) })
            canvas.DrawCircle(cx, cy, outer * 0.3f, label);
        using (var hole = new SKPaint { IsAntialias = true, Color = dark.WithAlpha(0xE0) })
            canvas.DrawCircle(cx, cy, outer * 0.035f, hole);
    }

    private static int CountBars(int length, Func<int, bool> isBar)
    {
        var count = 0;
        while (count < length / 2 && isBar(count)) count++;
        return count;
    }

    private static bool IsDarkLine(SKBitmap bitmap, int index, bool horizontal, int level)
    {
        var length = horizontal ? bitmap.Width : bitmap.Height;
        var samples = Math.Min(length, 64);
        var dark = 0;
        for (var s = 0; s < samples; s++)
        {
            var along = (int)((s + 0.5) * length / samples);
            var c = horizontal ? bitmap.GetPixel(along, index) : bitmap.GetPixel(index, along);
            if (Math.Max(c.Red, Math.Max(c.Green, c.Blue)) <= level) dark++;
        }
        return dark >= samples * 0.97;
    }
}
