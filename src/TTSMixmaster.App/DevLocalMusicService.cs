#if DEV_SOURCE
using System.Text.Json;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App;

/// <summary>
/// Verification builds only (<c>dotnet publish -p:TtsmDevSource=true</c>); never compiled into a release.
/// Stands in for YouTube with collections read from %LOCALAPPDATA%\TTSMixmaster\dev-collection.json, so a
/// full Build can run without music-service credentials. The file holds one collection object or an array of
/// them; each may set <c>Kind</c> (Playlist, Album or Chart) and <c>Owner</c>. Search matches names of the
/// requested kind. Tracks carry direct media URLs (e.g. a local HTTP server), which the real downloader
/// fetches with yt-dlp like any other source.
/// </summary>
internal sealed class DevLocalMusicService(string file) : IMusicService
{
    private sealed record DevTrack(string Title, string Artist, string Album, double DurationSeconds, string SourceUrl);
    private sealed record DevCollection(string Id, string Name, string Description, string ThumbnailUrl, DevTrack[] Tracks,
        CollectionKind Kind = CollectionKind.Playlist, string? Owner = null);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public ServiceKind Kind => ServiceKind.YouTube;
    public bool IsConfigured => File.Exists(file);

    public Task<string?> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(IsConfigured ? null : $"No local test collection at {file}.");

    public async Task<IReadOnlyList<CollectionSummary>> GetMyCollectionsAsync(CancellationToken ct = default) =>
        [.. (await LoadAllAsync(ct)).Select(c => c.Summary)];

    public async Task<IReadOnlyList<CollectionSummary>> SearchAsync(string query, CollectionKind kind, int limit = 20, CancellationToken ct = default) =>
        [.. (await LoadAllAsync(ct)).Select(c => c.Summary)
            .Where(s => s.Kind == kind && s.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).Take(limit)];

    public async Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default)
    {
        var all = await LoadAllAsync(ct);
        return all.FirstOrDefault(c => c.Summary.Id == idOrUrl || idOrUrl.Contains(c.Summary.Id, StringComparison.Ordinal)) ?? all[0];
    }

    private async Task<IReadOnlyList<MusicCollection>> LoadAllAsync(CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var dev = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.Deserialize<DevCollection[]>(Json)
            : [document.RootElement.Deserialize<DevCollection>(Json)!];
        if (dev is null || dev.Length == 0)
            throw new InvalidDataException($"{file} is empty.");
        return [.. dev.Select(Map)];
    }

    private static MusicCollection Map(DevCollection dev)
    {
        var tracks = dev.Tracks.Select((t, i) => new Track
        {
            Title = t.Title, Artist = t.Artist, Album = t.Album, Duration = TimeSpan.FromSeconds(t.DurationSeconds),
            SourceUrl = t.SourceUrl, ServiceId = $"dev-{i + 1}", Service = ServiceKind.YouTube, TrackNumber = i + 1,
        }).ToList();
        return new MusicCollection
        {
            Summary = new CollectionSummary
            {
                Id = dev.Id, Name = dev.Name, Service = ServiceKind.YouTube, Kind = dev.Kind,
                Owner = dev.Owner ?? "local test", Description = dev.Description, ThumbnailUrl = dev.ThumbnailUrl, TrackCount = tracks.Count,
            },
            Tracks = tracks,
        };
    }
}
#endif
