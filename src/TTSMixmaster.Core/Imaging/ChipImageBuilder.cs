using System.Security.Cryptography;
using SkiaSharp;
using TTSMixmaster.Core.Models;
using TTSMixmaster.Core.Services;

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
    /// The whole art fetch (own art, every mosaic cover and the fallback, headers and bodies) must finish within this.
    /// HttpClient.Timeout stops at the headers (ResponseHeadersRead), so a server that stalls mid-body would otherwise
    /// hang the download; one deadline for the whole fetch keeps stalled replacement covers from adding up.
    /// </summary>
    public static readonly TimeSpan DefaultArtTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _artTimeout = artTimeout ?? DefaultArtTimeout;

    /// <summary>How far the art is drawn past the window edge, so the frame's anti-aliased edge sits on art, not on nothing.</summary>
    private const float ArtBleed = 1.5f;

    private static readonly Lazy<ChipImage> Back = new(BackFromAssets);

    /// <summary>The chip back (unchanged from the original MWG chip), for <see cref="TtsOutputOptions.SecondaryImageUrl"/>.</summary>
    public static ChipImage BackImage => Back.Value;

    /// <summary>Downloads the collection's art (see <see cref="FetchArtAsync"/>) and renders the chip front.</summary>
    /// <remarks>Download or decode failures fall back to a generated background; only cancellation throws.</remarks>
    public async Task<ChipImage> BuildFrontAsync(MusicCollection collection, ChipOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        var fetch = await FetchArtAsync(collection, ct);
        using var art = ChipArt.Decode(fetch.Art);
        return Render(art, fetch.IsPrepared, Title(collection, options), collection.Summary.Service, options,
            art is null ? ChipArtSource.Generated : fetch.Source);
    }

    /// <summary>
    /// Downloads the art <see cref="BuildFrontAsync"/> would use without rendering, so a caller can re-render (title,
    /// options) without downloading again:
    /// <list type="number">
    /// <item>The collection's own art (<see cref="CollectionArtKind.Own"/> with a URL).</item>
    /// <item>Otherwise, or if that fails: the first four distinct track covers as a 2x2 mosaic (a PNG composed here);
    /// with one to three distinct covers, the first alone, as YouTube Music appears to.</item>
    /// <item>A <see cref="CollectionArtKind.ServiceDefault"/> thumbnail, if no track cover could be had.</item>
    /// </list>
    /// The whole fetch shares one deadline (the art timeout): when it passes, whatever covers loaded are used (the
    /// first alone if fewer than four), else no art. No art: the result says whether a download timed out (worth
    /// retrying later) or there simply was no usable art. Only cancellation of <paramref name="ct"/> throws.
    /// </summary>
    public async Task<ChipArtFetch> FetchArtAsync(MusicCollection collection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_artTimeout);
        var summary = collection.Summary;
        var timedOut = false;
        if (summary.ArtKind == CollectionArtKind.Own)
        {
            var (bytes, timeout) = await TryDownloadAsync(summary.ThumbnailUrl, deadline.Token, ct);
            timedOut |= timeout;
            if (IsSupportedArt(bytes)) return new(bytes, false, ChipArtSource.Collection);
        }

        var (covers, coversTimedOut) = await DownloadCoversAsync(collection.Tracks, deadline.Token, ct);
        timedOut |= coversTimedOut;
        if (covers.Count >= MosaicTiles) return new(ComposeMosaic(covers), false, ChipArtSource.Mosaic);
        if (covers.Count > 0) return new(covers[0], false, ChipArtSource.Track);

        if (summary.ArtKind == CollectionArtKind.ServiceDefault && !deadline.IsCancellationRequested)
        {
            var (bytes, timeout) = await TryDownloadAsync(summary.ThumbnailUrl, deadline.Token, ct);
            timedOut |= timeout;
            if (IsSupportedArt(bytes)) return new(bytes, false, ChipArtSource.Collection);
        }
        ct.ThrowIfCancellationRequested();
        return new(null, timedOut || deadline.IsCancellationRequested);
    }

    /// <summary>Tiles in a mosaic (2x2).</summary>
    private const int MosaicTiles = 4;

    /// <summary>At most this many distinct covers are tried for a mosaic, so a playlist of broken links stays cheap.</summary>
    internal const int MaxCoverAttempts = 12;

    /// <summary>Side of a composed mosaic in pixels (512 per tile): more than the window needs even at 2x.</summary>
    internal const int MosaicSize = 1024;

    /// <summary>
    /// Up to four usable covers, in track order, from the tracks' distinct thumbnails. Covers are downloaded in
    /// parallel; a failed one is replaced by the next distinct cover, at most <see cref="MaxCoverAttempts"/> in all.
    /// The result depends only on which downloads succeed, never on their timing. No batch starts once the fetch's
    /// <paramref name="deadline"/> has passed.
    /// </summary>
    private async Task<(List<byte[]> Covers, bool TimedOut)> DownloadCoversAsync(IEnumerable<Track> tracks, CancellationToken deadline, CancellationToken ct)
    {
        var candidates = DistinctCovers(tracks).Take(MaxCoverAttempts).ToArray();
        var covers = new List<byte[]>(MosaicTiles);
        var timedOut = false;
        var next = 0;
        while (covers.Count < MosaicTiles && next < candidates.Length && !deadline.IsCancellationRequested)
        {
            var batch = candidates.Skip(next).Take(MosaicTiles - covers.Count).ToArray();
            next += batch.Length;
            var downloads = await Task.WhenAll(batch.Select(url => TryDownloadAsync(url, deadline, ct)));
            foreach (var (bytes, timeout) in downloads)
            {
                timedOut |= timeout;
                if (bytes is not null && IsSupportedArt(bytes)) covers.Add(bytes);
            }
        }
        return (covers, timedOut);
    }

    /// <summary>
    /// Track thumbnail URLs in track order, without repeats: the same URL, or (YouTube) a thumbnail of the same video
    /// at another size, counts once. URLs that can't be fetched (no URL, other schemes) are skipped.
    /// </summary>
    internal static IEnumerable<string> DistinctCovers(IEnumerable<Track> tracks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (ArtUri(track.ThumbnailUrl) is not { } uri) continue;
            var key = YouTubeService.ThumbnailVideoId(track.ThumbnailUrl) is { } videoId ? "yt:" + videoId : uri.AbsoluteUri;
            if (seen.Add(key)) yield return track.ThumbnailUrl;
        }
    }

    /// <summary>
    /// The first four covers as a 2x2 PNG (top-left, top-right, bottom-left, bottom-right). Each tile is cover-fitted
    /// like a single cover: black letterbox/pillarbox bars are trimmed, then the centre square is used. The result is
    /// prepared art (<see cref="ChipArtFetch.IsPrepared"/>): rendering must not look for bars in it again, because dark
    /// edges of separate covers can line up like a letterbox.
    /// </summary>
    internal static byte[] ComposeMosaic(IReadOnlyList<byte[]> covers)
    {
        using var surface = SKSurface.Create(new SKImageInfo(MosaicSize, MosaicSize, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create a drawing surface.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        const int tile = MosaicSize / 2;
        for (var i = 0; i < MosaicTiles; i++)
        {
            using var art = ChipArt.Decode(covers[i]) ?? throw new ArgumentException("Every cover must be supported art.", nameof(covers));
            var cell = SKRect.Create(i % 2 * tile, i / 2 * tile, tile, tile);
            canvas.Save();
            canvas.ClipRect(cell);
            ChipArt.DrawCover(canvas, art, cell);
            canvas.Restore();
        }
        canvas.Flush();
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("PNG encoding failed.");
        return png.ToArray();
    }

    /// <summary>True when <paramref name="bytes"/> is art the chip renderer accepts (PNG, JPEG or WebP that decodes).</summary>
    public static bool IsSupportedArt(byte[]? bytes)
    {
        using var art = ChipArt.Decode(bytes);
        return art is not null;
    }

    /// <summary>Renders a chip front from art bytes already in hand (null or undecodable → generated background).</summary>
    /// <param name="artIsPrepared">
    /// True for art that is already trimmed and fitted (<see cref="ChipArtFetch.IsPrepared"/>: a mosaic). It is
    /// cover-fitted without looking for letterbox bars.
    /// </param>
    public static ChipImage RenderFront(byte[]? art, string title, ServiceKind? service, ChipOptions? options = null, bool artIsPrepared = false)
    {
        options ??= new ChipOptions();
        using var bitmap = ChipArt.Decode(art);
        return Render(bitmap, artIsPrepared, options.ShowTitle ? title : "", service, options,
            bitmap is null ? ChipArtSource.Generated : ChipArtSource.Collection);
    }

    private static string Title(MusicCollection collection, ChipOptions options) =>
        !options.ShowTitle ? "" : string.IsNullOrWhiteSpace(options.Title) ? collection.Name : options.Title;

    private static ChipImage Render(SKBitmap? art, bool artIsPrepared, string title, ServiceKind? service, ChipOptions options, ChipArtSource source)
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
            if (art is not null) ChipArt.DrawCover(canvas, art, target, trimBars: !artIsPrepared);
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

    /// <summary>
    /// Downloads art before <paramref name="deadline"/> (the whole fetch's, linked to <paramref name="ct"/>). Only the
    /// caller's cancellation throws; the deadline passing is a timeout.
    /// </summary>
    private async Task<(byte[]? Bytes, bool TimedOut)> TryDownloadAsync(string url, CancellationToken deadline, CancellationToken ct)
    {
        if (ArtUri(url) is not { } uri)
            return (null, false);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline);
            if (!response.IsSuccessStatusCode) return (null, false);
            if (response.Content.Headers.ContentLength > MaxArtBytes) return (null, false);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline)) > 0)
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
