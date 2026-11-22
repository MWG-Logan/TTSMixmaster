using System.Security.Cryptography;
using SkiaSharp;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Imaging;

/// <summary>
/// Renders the front image of a music chip: the collection's art behind the Morningwood Gaming chip
/// frame, with the collection name on top. Output is deterministic for the same art, title, options and
/// installed fonts (fonts only matter for characters Cinzel lacks).
/// </summary>
public sealed class ChipImageBuilder(HttpClient http, TimeSpan? artTimeout = null)
{
    /// <summary>Art larger than this is ignored rather than downloaded.</summary>
    public const long MaxArtBytes = 20 * 1024 * 1024;

    /// <summary>
    /// The whole art request, headers and body, must finish within this. HttpClient.Timeout stops at the
    /// headers (ResponseHeadersRead), so a server that stalls mid-body would otherwise hang the download.
    /// </summary>
    public static readonly TimeSpan DefaultArtTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _artTimeout = artTimeout ?? DefaultArtTimeout;

    /// <summary>How far the art is drawn past the window edge, so the frame's anti-aliased edge sits on art, not on nothing.</summary>
    private const float ArtBleed = 1.5f;

    private static readonly Lazy<ChipImage> Back = new(BackFromAssets);

    /// <summary>The chip back (unchanged from the original MWG chip), for <see cref="TtsOutputOptions.SecondaryImageUrl"/>.</summary>
    public static ChipImage BackImage => Back.Value;

    /// <summary>Downloads the collection's art (falling back to its first track's) and renders the chip front.</summary>
    /// <remarks>Download or decode failures fall back to a generated background; only cancellation throws.</remarks>
    public async Task<ChipImage> BuildFrontAsync(MusicCollection collection, ChipOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();

        var candidates = new List<(string Url, ChipArtSource Source)> { (collection.Summary.ThumbnailUrl, ChipArtSource.Collection) };
        var track = collection.Tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ThumbnailUrl));
        if (track is not null) candidates.Add((track.ThumbnailUrl, ChipArtSource.Track));

        foreach (var (url, source) in candidates)
        {
            var (bytes, _) = await TryDownloadAsync(url, ct);
            using var art = ChipArt.Decode(bytes);
            if (art is null) continue;
            ct.ThrowIfCancellationRequested();
            return Render(art, Title(collection, options), collection.Summary.Service, options, source);
        }
        ct.ThrowIfCancellationRequested();
        return Render(null, Title(collection, options), collection.Summary.Service, options, ChipArtSource.Generated);
    }

    /// <summary>
    /// Downloads the art <see cref="BuildFrontAsync"/> would use (the collection's, else its first track's) without
    /// rendering, so a caller can re-render (title, options) without downloading again. No art: the result says
    /// whether a download timed out (worth retrying later) or there simply was no usable art.
    /// </summary>
    public async Task<ChipArtFetch> FetchArtAsync(MusicCollection collection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var track = collection.Tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.ThumbnailUrl));
        var timedOut = false;
        foreach (var url in new[] { collection.Summary.ThumbnailUrl, track?.ThumbnailUrl })
        {
            var (bytes, timeout) = await TryDownloadAsync(url ?? "", ct);
            timedOut |= timeout;
            using var art = ChipArt.Decode(bytes);
            if (art is not null) return new(bytes, false);
        }
        ct.ThrowIfCancellationRequested();
        return new(null, timedOut);
    }

    /// <summary>True when <paramref name="bytes"/> is art the chip renderer accepts (PNG, JPEG or WebP that decodes).</summary>
    public static bool IsSupportedArt(byte[]? bytes)
    {
        using var art = ChipArt.Decode(bytes);
        return art is not null;
    }

    /// <summary>Renders a chip front from art bytes already in hand (null or undecodable → generated background).</summary>
    public static ChipImage RenderFront(byte[]? art, string title, ServiceKind? service, ChipOptions? options = null)
    {
        options ??= new ChipOptions();
        using var bitmap = ChipArt.Decode(art);
        return Render(bitmap, options.ShowTitle ? title : "", service, options,
            bitmap is null ? ChipArtSource.Generated : ChipArtSource.Collection);
    }

    private static string Title(MusicCollection collection, ChipOptions options) =>
        !options.ShowTitle ? "" : string.IsNullOrWhiteSpace(options.Title) ? collection.Name : options.Title;

    private static ChipImage Render(SKBitmap? art, string title, ServiceKind? service, ChipOptions options, ChipArtSource source)
    {
        if (options.Scale is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(options), options.Scale, "Chip scale must be 1 or 2.");
        var pixels = ChipGeometry.Size * options.Scale;

        using var surface = SKSurface.Create(new SKImageInfo(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create a drawing surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(options.Scale);

        using var typesetter = new ChipTitle(ChipAssets.TitleTypeface);
        var layout = typesetter.Fit(title);

        // Window content: art (or a generated backdrop) plus, under a title, a scrim for legibility.
        using (var window = ChipGeometry.WindowPath(ArtBleed))
        {
            canvas.Save();
            canvas.ClipPath(window, SKClipOperation.Intersect, antialias: true);
            var target = ChipGeometry.WindowBounds;
            target.Inflate(ArtBleed, ArtBleed);
            if (art is not null) ChipArt.DrawCover(canvas, art, target);
            else ChipArt.DrawGenerated(canvas, target, service);
            if (layout is not null) DrawScrim(canvas, target, layout);
            canvas.Restore();
        }

        // The frame, then the title on top.
        var sampling = options.Scale == 1 ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKCubicResampler.Mitchell);
        canvas.DrawImage(ChipAssets.Frame, new SKRect(0, 0, ChipGeometry.Size, ChipGeometry.Size), sampling, null);
        if (layout is not null) typesetter.Draw(canvas, layout);

        canvas.Flush();
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("PNG encoding failed.");
        return Image(png.ToArray(), pixels, pixels, source);
    }

    /// <summary>Darkens the lower part of the window, from clear above the title to ~70% black at the bottom edge.</summary>
    private static void DrawScrim(SKCanvas canvas, SKRect target, ChipTitle.Layout layout)
    {
        var top = ChipTitle.LastLineBaseline - layout.Size * ChipTitle.LineSpacing * layout.Lines.Count - 70;
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, top), new SKPoint(0, target.Bottom),
            [SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(0x60), SKColors.Black.WithAlpha(0xB4)],
            [0, 0.45f, 1], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(new SKRect(target.Left, top, target.Right, target.Bottom), paint);
    }

    /// <summary>Downloads art within the art timeout for the whole request. Only the caller's cancellation throws.</summary>
    private async Task<(byte[]? Bytes, bool TimedOut)> TryDownloadAsync(string url, CancellationToken ct)
    {
        if (ArtUri(url) is not { } uri)
            return (null, false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_artTimeout);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) return (null, false);
            if (response.Content.Headers.ContentLength > MaxArtBytes) return (null, false);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxArtBytes) return (null, false);
                buffer.Write(chunk, 0, read);
            }
            return (buffer.ToArray(), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            // Our deadline, or HttpClient.Timeout while waiting for headers: the server took too long.
            return (null, true);
        }
        catch (Exception)
        {
            // Network errors, bad responses: any art problem just means the generated background.
            return (null, false);
        }
    }

    /// <summary>
    /// The art URL to fetch, https only: a plain-http thumbnail (older service CDNs) is upgraded to https
    /// rather than fetched in the clear. Anything else (no URL, other schemes) is null.
    /// </summary>
    internal static Uri? ArtUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme == Uri.UriSchemeHttps)
            return uri;
        return uri.Scheme == Uri.UriSchemeHttp
            ? new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri
            : null;
    }

    /// <summary>
    /// A square thumbnail of a rendered chip as PNG (TTS's Saved Objects thumbnail is 256×256).
    /// </summary>
    public static byte[] Thumbnail(ChipImage chip, int size = 256)
    {
        ArgumentNullException.ThrowIfNull(chip);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 16);
        using var source = SKBitmap.Decode(chip.Png) ?? throw new InvalidOperationException("The chip image is not a valid PNG.");
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create a drawing surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(source);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, size, size), new SKSamplingOptions(SKCubicResampler.Mitchell), null);
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("PNG encoding failed.");
        return png.ToArray();
    }

    private static ChipImage BackFromAssets()
    {
        using var codec = SKCodec.Create(new SKMemoryStream(ChipAssets.Back))
            ?? throw new InvalidOperationException("The embedded chip back is not a valid PNG.");
        return Image(ChipAssets.Back, codec.Info.Width, codec.Info.Height, ChipArtSource.None);
    }

    private static ChipImage Image(byte[] png, int width, int height, ChipArtSource source) =>
        new(png, Convert.ToHexStringLower(SHA256.HashData(png)), width, height, source);
}
