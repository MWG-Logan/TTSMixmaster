using SkiaSharp;

namespace TTSMixmaster.Core.Imaging;

/// <summary>The chip art and title font embedded in this assembly (see TTSMixmaster.Core.csproj).</summary>
internal static class ChipAssets
{
    private static readonly Lazy<byte[]> FrontPng = new(() => Read("Chip/front.png"));
    private static readonly Lazy<byte[]> BackPng = new(() => Read("Chip/back.png"));
    private static readonly Lazy<byte[]> FramePng = new(() => Read("Chip/frame.png"));
    private static readonly Lazy<SKImage> FrameImage = new(() =>
        SKImage.FromEncodedData(FramePng.Value) ?? throw new InvalidOperationException("The embedded chip frame is not a valid PNG."));
    private static readonly Lazy<SKTypeface> Typeface = new(() =>
    {
        using var stream = new MemoryStream(Read("Chip/Cinzel-Bold.ttf"));
        return SKFontManager.Default.CreateTypeface(stream, 0)
            ?? throw new InvalidOperationException("The embedded chip title font could not be loaded.");
    });

    internal static byte[] Front => FrontPng.Value;
    internal static byte[] Back => BackPng.Value;
    internal static byte[] FrameBytes => FramePng.Value;
    internal static SKImage Frame => FrameImage.Value;
    internal static SKTypeface TitleTypeface => Typeface.Value;

    private static byte[] Read(string name)
    {
        using var stream = typeof(ChipAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
