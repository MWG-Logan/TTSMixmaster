using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core;

// Seams between Core and the App. Implementations live in Core; the App depends only on these.
// Change a signature here only with the coordinator's agreement: several workers build against them.

/// <summary>One music service (Last.fm, YouTube, Spotify).</summary>
public interface IMusicService
{
    ServiceKind Kind { get; }

    /// <summary>True when the credentials it needs are filled in (not whether they work).</summary>
    bool IsConfigured { get; }

    /// <summary>Checks the credentials against the live API. Returns null on success, else a user-readable reason.</summary>
    Task<string?> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>The signed-in/configured user's own collections (playlists, liked, charts).</summary>
    Task<IReadOnlyList<CollectionSummary>> GetMyCollectionsAsync(CancellationToken ct = default);

    /// <summary>Public search. Services that can't search return an empty list.</summary>
    Task<IReadOnlyList<CollectionSummary>> SearchAsync(string query, CollectionKind kind, int limit = 20, CancellationToken ct = default);

    /// <summary>Loads a collection's tracks. Accepts a <see cref="CollectionSummary.Id"/> or a pasted share URL.</summary>
    Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default);
}

public enum TrackStatus { Pending, Working, Done, Skipped, Failed }

/// <summary>Progress for one track inside a batch.</summary>
public sealed record TrackProgress(int Index, int Total, Track Track, TrackStatus Status, string? Message = null);

public sealed record DownloadedTrack(Track Track, string? FilePath, TrackStatus Status, string? Error = null);

/// <summary>Finds and downloads audio for tracks (yt-dlp + ffmpeg), tagging the resulting MP3s.</summary>
public interface IAudioDownloader
{
    /// <summary>Null when yt-dlp/ffmpeg are usable; else what's missing.</summary>
    Task<string?> CheckToolsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DownloadedTrack>> DownloadAsync(
        MusicCollection collection,
        IProgress<TrackProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record UploadedTrack(Track Track, string FilePath, string? PublicUrl, TrackStatus Status, string? Error = null);

/// <summary>Uploads audio files and returns public URLs TTS can stream.</summary>
public interface IAudioUploader
{
    bool IsConfigured { get; }
    Task<string?> TestConnectionAsync(CancellationToken ct = default);

    Task<IReadOnlyList<UploadedTrack>> UploadAsync(
        IReadOnlyList<DownloadedTrack> files,
        IProgress<TrackProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed record TtsOutputOptions
{
    public string ObjectNickname { get; init; } = "";
    public string Description { get; init; } = "";
    public string PrimaryImageUrl { get; init; } = "";
    public string SecondaryImageUrl { get; init; } = "";
}

/// <summary>Generated TTS artifacts and where they were written.</summary>
public sealed record TtsOutput(IReadOnlyList<string> WrittenFiles, string LuaScript);

/// <summary>Generates Tabletop Simulator Lua/JSON/save files for an uploaded collection.</summary>
public interface ITtsFormatter
{
    Task<TtsOutput> GenerateAsync(
        MusicCollection collection,
        IReadOnlyList<UploadedTrack> uploads,
        TtsOutputOptions options,
        CancellationToken ct = default);
}
