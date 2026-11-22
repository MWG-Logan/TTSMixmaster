using SkiaSharp;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Imaging;

/// <summary>Decodes, trims and cover-fits the art behind the chip frame, or paints a generated background.</summary>
internal static class ChipArt
{
    /// <summary>Larger sources are decoded downscaled (where the codec supports it): the window is only ~415px.</summary>
    private const int MaxDecodeSize = 2048;

    /// <summary>
    /// Decodes art, returning null for anything that isn't a usable PNG, JPEG or WebP image.
    /// Art is untrusted internet input, so every other format Skia could decode (GIF, BMP, ICO, WBMP,
    /// HEIF, AVIF, …) is rejected by its signature before any codec sees it, and again by the codec's
    /// detected format before any pixels are decoded.
    /// </summary>
    internal static SKBitmap? Decode(byte[]? bytes)
    {
        if (bytes is null || !HasAllowedSignature(bytes)) return null;
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return null;
        if (codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp)) return null;

        var size = codec.Info.Size;
        var longest = Math.Max(size.Width, size.Height);
        if (longest > MaxDecodeSize) size = codec.GetScaledDimensions((float)MaxDecodeSize / longest);

        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return bitmap;
        bitmap.Dispose();
        return null;
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
        var top = CountBars(h, i => IsBlackLine(bitmap, i, horizontal: true));
        var bottom = CountBars(h, i => IsBlackLine(bitmap, h - 1 - i, horizontal: true));
        var left = CountBars(w, i => IsBlackLine(bitmap, i, horizontal: false));
        var right = CountBars(w, i => IsBlackLine(bitmap, w - 1 - i, horizontal: false));

        // Only trim matched pairs of bars that leave a substantial picture; a dark sky is not a letterbox.
        var trimRows = top > 0 && bottom > 0 && Math.Abs(top - bottom) <= h / 50 + 2 && h - top - bottom >= h * 2 / 5;
        var trimCols = left > 0 && right > 0 && Math.Abs(left - right) <= w / 50 + 2 && w - left - right >= w * 2 / 5;
        return new SKRectI(trimCols ? left : 0, trimRows ? top : 0, trimCols ? w - right : w, trimRows ? h - bottom : h);
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

    /// <summary>Draws the art cover-fitted (centre-cropped) into <paramref name="target"/>.</summary>
    internal static void DrawCover(SKCanvas canvas, SKBitmap art, SKRect target)
    {
        var content = ContentBounds(art);
        var crop = CoverCrop(new SKRect(content.Left, content.Top, content.Right, content.Bottom), target);
        using var image = SKImage.FromBitmap(art);
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

    private static bool IsBlackLine(SKBitmap bitmap, int index, bool horizontal)
    {
        var length = horizontal ? bitmap.Width : bitmap.Height;
        var samples = Math.Min(length, 64);
        var dark = 0;
        for (var s = 0; s < samples; s++)
        {
            var along = (int)((s + 0.5) * length / samples);
            var c = horizontal ? bitmap.GetPixel(along, index) : bitmap.GetPixel(index, along);
            if (Math.Max(c.Red, Math.Max(c.Green, c.Blue)) <= 24) dark++;
        }
        return dark >= samples * 0.97;
    }
}
