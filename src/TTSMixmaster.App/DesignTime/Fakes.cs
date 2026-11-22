using System.Text;
using TTSMixmaster.Core;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Files;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.App.DesignTime;

// Stand-ins for the Core implementations while they're being written.
// They behave like the real thing from the UI's point of view: realistic delays,
// configuration checks against the real settings, and a couple of deliberate failures.

internal sealed class FakeMusicService(ServiceKind kind, Func<AppSettings> settings) : IMusicService
{
    public ServiceKind Kind => kind;

    public bool IsConfigured
    {
        get
        {
            var s = settings();
            return kind switch
            {
                ServiceKind.LastFm => Has(s.LastfmApiKey) && Has(s.LastfmUsername),
                ServiceKind.YouTube => Has(s.YoutubeApiKey),
                ServiceKind.Spotify => Has(s.SpotifyClientId) && Has(s.SpotifyClientSecret),
                _ => false,
            };
        }
    }

    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        await Task.Delay(900, ct);
        var s = settings();
        return kind switch
        {
            ServiceKind.LastFm when s.LastfmApiKey.Trim().Length != 32 =>
                "Last.fm didn't accept that API key. Keys are 32 characters; copy yours from last.fm/api/accounts.",
            ServiceKind.YouTube when !s.YoutubeApiKey.StartsWith("AIza", StringComparison.Ordinal) =>
                "YouTube didn't accept that API key. Create one for the YouTube Data API v3 in Google Cloud Console.",
            ServiceKind.Spotify when !IsConfigured =>
                "Spotify needs both a client ID and a client secret.",
            _ => null,
        };
    }

    public async Task<IReadOnlyList<CollectionSummary>> GetMyCollectionsAsync(CancellationToken ct = default)
    {
        await Task.Delay(kind == ServiceKind.Spotify ? 1100 : 700, ct);
        var s = settings();
        if (kind == ServiceKind.YouTube && !Has(s.YoutubeChannelId))
            throw new InvalidOperationException("Add your channel ID in Settings to list your YouTube playlists.");
        var owner = kind switch
        {
            ServiceKind.LastFm => s.LastfmUsername,
            ServiceKind.Spotify => Has(s.SpotifyUserId) ? s.SpotifyUserId : "you",
            _ => "you",
        };
        return SampleData.MyCollections(kind, owner);
    }

    public async Task<IReadOnlyList<CollectionSummary>> SearchAsync(string query, CollectionKind kind_, int limit = 20, CancellationToken ct = default)
    {
        await Task.Delay(600, ct);
        return kind == ServiceKind.LastFm && kind_ == CollectionKind.Playlist
            ? [] // Last.fm has no public playlists.
            : SampleData.Search(kind, query, kind_).Take(limit).ToList();
    }

    public async Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default)
    {
        await Task.Delay(800, ct);
        var known = SampleData.MyCollections(kind, settings().LastfmUsername).FirstOrDefault(c => c.Id == idOrUrl);
        var summary = known
            ?? (idOrUrl.Contains(":search:", StringComparison.Ordinal)
                ? SampleData.Search(kind, idOrUrl.Split(':')[2], CollectionKind.Playlist)
                    .FirstOrDefault(c => c.Id == idOrUrl)
                : null)
            ?? SampleData.FromLink(kind, idOrUrl);
        return SampleData.Collection(summary);
    }

    private static bool Has(string value) => !string.IsNullOrWhiteSpace(value);
}

internal sealed class FakeAudioDownloader : IAudioDownloader
{
    public Task<string?> CheckToolsAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    public async Task<IReadOnlyList<DownloadedTrack>> DownloadAsync(
        MusicCollection collection, IProgress<TrackProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<DownloadedTrack>();
        var total = collection.Tracks.Count;
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var track = collection.Tracks[i];
            progress?.Report(new TrackProgress(i, total, track, TrackStatus.Working, "Finding audio…"));
            await Task.Delay(700 + (i * 97) % 400, ct);

            DownloadedTrack result = (i % 9) switch
            {
                4 => new(track, null, TrackStatus.Failed, "No match within 10 seconds of the track's length."),
                7 when i > 9 => new(track, null, TrackStatus.Failed, "The best match is age-restricted on YouTube."),
                2 => new(track, $@"C:\Music\{LibraryLayout.TrackFileName(track)}", TrackStatus.Skipped, "Already downloaded"),
                _ => new(track, $@"C:\Music\{LibraryLayout.TrackFileName(track)}", TrackStatus.Done),
            };
            results.Add(result);
            progress?.Report(new TrackProgress(i, total, track, result.Status, result.Error ?? (result.Status == TrackStatus.Skipped ? "Already downloaded" : null)));
        }
        return results;
    }
}

internal sealed class FakeAudioUploader(Func<AppSettings> settings, Func<bool> hasSignedInTarget) : IAudioUploader
{
    public bool IsConfigured => hasSignedInTarget() || !string.IsNullOrWhiteSpace(settings().AzureStorageConnectionString);

    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        await Task.Delay(1000, ct);
        var cs = settings().AzureStorageConnectionString;
        if (!cs.Contains("AccountName=", StringComparison.OrdinalIgnoreCase))
            return "That doesn't look like a storage connection string. Copy it from the storage account's Access keys page.";
        return null;
    }

    public async Task<IReadOnlyList<UploadedTrack>> UploadAsync(
        IReadOnlyList<DownloadedTrack> files, IProgress<TrackProgress>? progress = null, CancellationToken ct = default)
    {
        var results = new List<UploadedTrack>();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            progress?.Report(new TrackProgress(i, files.Count, file.Track, TrackStatus.Working, "Uploading…"));
            await Task.Delay(140, ct);
            var url = $"https://ttsmixmaster.blob.core.windows.net/{settings().AzureContainerName}/{Uri.EscapeDataString(Path.GetFileName(file.FilePath!))}";
            var fresh = i % 5 == 3;
            results.Add(new UploadedTrack(file.Track, file.FilePath!, url, fresh ? TrackStatus.Skipped : TrackStatus.Done));
            progress?.Report(new TrackProgress(i, files.Count, file.Track, fresh ? TrackStatus.Skipped : TrackStatus.Done,
                fresh ? "Already uploaded" : null));
        }
        return results;
    }
}

internal sealed class FakeTtsFormatter(LibraryLayout layout) : ITtsFormatter
{
    public async Task<TtsOutput> GenerateAsync(
        MusicCollection collection, IReadOnlyList<UploadedTrack> uploads, TtsOutputOptions options, CancellationToken ct = default)
    {
        await Task.Delay(900, ct);
        var lua = new StringBuilder()
            .AppendLine($"-- {(string.IsNullOrWhiteSpace(options.ObjectNickname) ? collection.Name : options.ObjectNickname)}")
            .AppendLine("-- Generated by TTSMixmaster (design-time sample)")
            .AppendLine("local playlist = {");
        foreach (var u in uploads)
            lua.AppendLine($"  {{ title = \"{u.Track.Artist} - {u.Track.Title}\", url = \"{u.PublicUrl}\" }},");
        lua.AppendLine("}")
            .AppendLine("function onLoad() MusicPlayer.setPlaylist(playlist) end");

        var folder = layout.TtsFolder(collection);
        Directory.CreateDirectory(folder);
        var luaPath = Path.Combine(folder, LibraryLayout.SanitizeFileName(collection.Name) + ".lua");
        await File.WriteAllTextAsync(luaPath, lua.ToString(), ct);
        return new TtsOutput([luaPath], lua.ToString());
    }
}
