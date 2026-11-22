// Regenerates the app icon from the chip's look: a slate chip with the notched bottom-left corner and a ♫.
//
//   dotnet run scripts/derive-app-icon.cs
//
// Writes src/TTSMixmaster.App/Assets/AppIcon.ico (16–256 px, PNG frames) and re-renders every PNG in
// installer/msix/Assets at its existing size (AppList/StoreLogo full-bleed; tiles with the icon centred).
// Colours are the chip frame's slate (#262632 → #191921, see src/TTSMixmaster.Core/Imaging/README.md).
#:project ../src/TTSMixmaster.Core/TTSMixmaster.Core.csproj
#:property UseAppHost=false
#:property PublishAot=false

using System.Buffers.Binary;
using SkiaSharp;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", ".."));
var icoPath = Path.Combine(root, "src", "TTSMixmaster.App", "Assets", "AppIcon.ico");
var msixAssets = Path.Combine(root, "installer", "msix", "Assets");

using var glyphFace = SKTypeface.FromFamilyName("Segoe UI Symbol", SKFontStyle.Bold)
    ?? throw new InvalidOperationException("Segoe UI Symbol is required (Windows).");

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
WriteIco(icoPath, icoSizes.Select(s => (s, Render(s, s, fullBleed: true))).ToList());
Console.WriteLine($"wrote {icoPath}");

if (Directory.Exists(msixAssets))
{
    foreach (var file in Directory.EnumerateFiles(msixAssets, "*.png"))
    {
        int w, h;
        using (var codec = SKCodec.Create(file)) (w, h) = (codec.Info.Width, codec.Info.Height);
        var name = Path.GetFileName(file);
        var fullBleed = name.StartsWith("AppList", StringComparison.OrdinalIgnoreCase) || name.StartsWith("StoreLogo", StringComparison.OrdinalIgnoreCase);
        File.WriteAllBytes(file, Render(w, h, fullBleed));
    }
    Console.WriteLine($"re-rendered {msixAssets}");
}

byte[] Render(int width, int height, bool fullBleed)
{
    using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    var size = fullBleed ? Math.Min(width, height) : Math.Min(width, height) * 0.6f;
    canvas.Translate((width - size) / 2f, (height - size) / 2f);
    DrawIcon(canvas, size);
    using var image = surface.Snapshot();
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    return png.ToArray();
}

void DrawIcon(SKCanvas canvas, float s)
{
    // Small sizes get a little less inset and a bigger glyph so the note stays legible at 16 px.
    var small = s < 40;
    var inset = small ? s * 0.02f : s * 0.04f;
    var body = new SKRect(inset, inset, s - inset, s - inset);
    var radius = s * 0.16f;
    var notch = s * (small ? 0.2f : 0.24f);

    var roundedBuilder = new SKPathBuilder();
    roundedBuilder.AddRoundRect(new SKRoundRect(body, radius, radius));
    using var rounded = roundedBuilder.Detach();
    using var corner = Polygon(new(body.Left - 1, body.Bottom - notch), new(body.Left + notch, body.Bottom + 1), new(body.Left - 1, body.Bottom + 1));
    using var chip = rounded.Op(corner, SKPathOp.Difference);

    using var fill = new SKPaint { IsAntialias = true };
    fill.Shader = SKShader.CreateLinearGradient(new SKPoint(0, body.Top), new SKPoint(0, body.Bottom),
        [new SKColor(0x3A, 0x3A, 0x48), new SKColor(0x19, 0x19, 0x21)], SKShaderTileMode.Clamp);
    canvas.DrawPath(chip, fill);

    if (!small)
    {
        // The chip's inner window edge: a thin lighter line, also notched.
        using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, s * 0.02f), Color = new SKColor(0x6E, 0x6E, 0x82) };
        var w = s * 0.12f;
        var inner = new SKRect(w, w, s - w, s - w);
        var n = notch * 0.75f;
        using var innerPath = Polygon(new(inner.Left, inner.Top), new(inner.Right, inner.Top), new(inner.Right, inner.Bottom),
            new(inner.Left + n, inner.Bottom), new(inner.Left, inner.Bottom - n));
        canvas.DrawPath(innerPath, edge);
    }

    using var font = new SKFont(glyphFace, s * (small ? 0.78f : 0.6f)) { Subpixel = true, Edging = SKFontEdging.Antialias };
    using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0xF4, 0xF1, 0xEA) };
    const string note = "♫";
    font.MeasureText(note, out var bounds, ink);
    canvas.DrawText(note, s / 2f - bounds.MidX, s / 2f - bounds.MidY, SKTextAlign.Left, font, ink);
}

static SKPath Polygon(params SKPoint[] points)
{
    var builder = new SKPathBuilder();
    builder.MoveTo(points[0].X, points[0].Y);
    foreach (var point in points.Skip(1))
        builder.LineTo(point.X, point.Y);
    builder.Close();
    return builder.Detach();
}

static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> frames)
{
    using var stream = File.Create(path);
    Span<byte> header = stackalloc byte[6];
    Span<byte> entry = stackalloc byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 1);              // type: icon
    BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)frames.Count);
    stream.Write(header);
    var offset = 6 + 16 * frames.Count;
    foreach (var (size, png) in frames)
    {
        entry.Clear();
        entry[0] = (byte)(size >= 256 ? 0 : size);
        entry[1] = (byte)(size >= 256 ? 0 : size);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);            // planes
        BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);           // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
        stream.Write(entry);
        offset += png.Length;
    }
    foreach (var (_, png) in frames)
        stream.Write(png);
}
