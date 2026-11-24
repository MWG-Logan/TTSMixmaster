namespace TTSMixmaster.Core.Models;

/// <summary>A music service the app can pull collections from.</summary>
public enum ServiceKind
{
    LastFm,
    YouTube,
    Spotify,
}

/// <summary>What a collection is, so the UI can label it honestly.</summary>
public enum CollectionKind
{
    /// <summary>A real, user-curated playlist (Spotify/YouTube playlists, liked songs, watch later).</summary>
    Playlist,

    /// <summary>A released album.</summary>
    Album,

    /// <summary>A computed list rather than a playlist (Last.fm top/loved/recent tracks, Spotify top tracks).</summary>
    Chart,
}

/// <summary>Whether a collection's <see cref="CollectionSummary.ThumbnailUrl"/> is art of its own.</summary>
/// <remarks>An empty <see cref="CollectionSummary.ThumbnailUrl"/> always means "no art of its own", whatever this says.</remarks>
public enum CollectionArtKind
{
    /// <summary>The collection's own image: album cover, custom playlist image, or a mosaic the service made (Spotify).</summary>
    Own,

    /// <summary>
    /// A stand-in the service filled in, not chosen for the collection: a YouTube playlist without a custom
    /// thumbnail reports its first video's thumbnail. Chip art builds a track mosaic instead.
    /// </summary>
    ServiceDefault,
}

/// <summary>One track from any service.</summary>
public sealed record Track
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string Album { get; init; } = "";
    public TimeSpan Duration { get; init; }

    /// <summary>Direct media/watch URL when the service has one (YouTube); otherwise empty and the downloader searches.</summary>
    public string SourceUrl { get; init; } = "";

    /// <summary>Service-specific track id.</summary>
    public string ServiceId { get; init; } = "";

    public ServiceKind Service { get; init; }
    public int PlayCount { get; init; }
    public string ThumbnailUrl { get; init; } = "";

    /// <summary>1-based position within an album or playlist, when known.</summary>
    public int? TrackNumber { get; init; }

    public override string ToString() => $"{Artist} - {Title}";
}

/// <summary>A list entry for a collection the user can open: enough to show it and fetch its tracks later.</summary>
public sealed record CollectionSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ServiceKind Service { get; init; }
    public required CollectionKind Kind { get; init; }
    public string Owner { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>The collection's image; empty when it has none (Spotify Liked/Top/Recent, Last.fm charts).</summary>
    public string ThumbnailUrl { get; init; } = "";

    /// <summary>Whether <see cref="ThumbnailUrl"/> is the collection's own art or a service stand-in.</summary>
    public CollectionArtKind ArtKind { get; init; }

    public int? TrackCount { get; init; }
}

/// <summary>A collection with its tracks loaded.</summary>
public sealed record MusicCollection
{
    public required CollectionSummary Summary { get; init; }
    public required IReadOnlyList<Track> Tracks { get; init; }

    public string Name => Summary.Name;
}
