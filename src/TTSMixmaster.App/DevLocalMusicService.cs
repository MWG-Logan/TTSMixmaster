#if DEV_SOURCE
using System.Text.Json;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App;

/// <summary>
/// Verification builds only (<c>dotnet publish -p:TtsmDevSource=true</c>); never compiled into a release.
/// Stands in for YouTube with one collection read from %LOCALAPPDATA%\TTSMixmaster\dev-collection.json, so a
/// full Build can run without music-service credentials. Tracks carry direct media URLs (e.g. a local HTTP
/// server), which the real downloader fetches with yt-dlp like any other source.
/// </summary>
internal sealed class DevLocalMusicService(string file) : IMusicService
{
    private sealed record DevTrack(string Title, string Artist, string Album, double DurationSeconds, string SourceUrl);
    private sealed record DevCollection(string Id, string Name, string Description, string ThumbnailUrl, DevTrack[] Tracks);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public ServiceKind Kind => ServiceKind.YouTube;
    public bool IsConfigured => File.Exists(file);

    public Task<string?> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(IsConfigured ? null : $"No local test collection at {file}.");

    public async Task<IReadOnlyList<CollectionSummary>> GetMyCollectionsAsync(CancellationToken ct = default) =>
        [(await LoadAsync(ct)).Summary];

    public Task<IReadOnlyList<CollectionSummary>> SearchAsync(string query, CollectionKind kind, int limit = 20, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CollectionSummary>>([]);

    public Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default) => LoadAsync(ct);

    private async Task<MusicCollection> LoadAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var dev = await JsonSerializer.DeserializeAsync<DevCollection>(stream, Json, ct)
            ?? throw new InvalidDataException($"{file} is empty.");
        var tracks = dev.Tracks.Select((t, i) => new Track
        {
            Title = t.Title, Artist = t.Artist, Album = t.Album, Duration = TimeSpan.FromSeconds(t.DurationSeconds),
            SourceUrl = t.SourceUrl, ServiceId = $"dev-{i + 1}", Service = ServiceKind.YouTube, TrackNumber = i + 1,
        }).ToList();
        return new MusicCollection
        {
            Summary = new CollectionSummary
            {
                Id = dev.Id, Name = dev.Name, Service = ServiceKind.YouTube, Kind = CollectionKind.Playlist,
                Owner = "local test", Description = dev.Description, ThumbnailUrl = dev.ThumbnailUrl, TrackCount = tracks.Count,
            },
            Tracks = tracks,
        };
    }
}
#endif
