namespace TTSMixmaster.Core.Imaging;

/// <summary>How a chip front is rendered.</summary>
public sealed record ChipOptions
{
    /// <summary>Draw the collection name over the art.</summary>
    public bool ShowTitle { get; init; } = true;

    /// <summary>Text to show instead of the collection name; null or blank uses the name.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// Output scale: 1 renders the native 478x478 (the default), 2 renders 956x956. The frame art only
    /// exists at 478, so at 2x it is resampled while the album art and title are drawn at full resolution.
    /// </summary>
    public int Scale { get; init; } = 1;
}

/// <summary>Where the art behind the frame came from.</summary>
public enum ChipArtSource
{
    /// <summary>Not a rendered front (the fixed chip back).</summary>
    None,

    /// <summary>The collection's own thumbnail (album art, playlist icon).</summary>
    Collection,

    /// <summary>One track's cover: the collection has no art of its own and fewer than four distinct track covers.</summary>
    Track,

    /// <summary>A 2x2 mosaic of the first four distinct track covers (the collection has no art of its own).</summary>
    Mosaic,

    /// <summary>No usable art: a generated background.</summary>
    Generated,
}

/// <summary>
/// The art <see cref="ChipImageBuilder.FetchArtAsync"/> found: its bytes (a composed PNG for a mosaic) and where
/// they came from, or null with whether a download timed out (so the caller can say so and retry later rather than
/// remember "no art").
/// </summary>
public sealed record ChipArtFetch(byte[]? Art, bool TimedOut, ChipArtSource Source = ChipArtSource.Generated);

/// <summary>A rendered chip face: PNG bytes and their SHA-256, which doubles as a content-addressed blob name.</summary>
public sealed record ChipImage(byte[] Png, string Hash, int Width, int Height, ChipArtSource ArtSource)
{
    public const string ContentType = "image/png";

    /// <summary>A stable, content-addressed file name, e.g. <c>chip-3f2a….png</c>.</summary>
    public string FileName => $"chip-{Hash}.png";
}
