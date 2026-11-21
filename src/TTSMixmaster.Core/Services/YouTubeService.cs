using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Services;

/// <summary>Public YouTube playlists using an API key. IDs are raw playlist IDs, including legacy LL and WL entries.</summary>
public sealed class YouTubeService(Func<AppSettings> settings, HttpClient httpClient) : IMusicService
{
    public YouTubeService(AppSettings settings, HttpClient httpClient) : this(() => settings, httpClient) { }
    public ServiceKind Kind => ServiceKind.YouTube;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings().YoutubeApiKey);

    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            // Unlike search.list, this check costs only one quota unit.
            await RequestAsync("videos", new() { ["part"] = "id", ["id"] = "dQw4w9WgXcQ" }, ct);
            return null;
        }
        catch (ServiceException ex) { return ex.Message; }
    }

    public async Task<IReadOnlyList<CollectionSummary>> GetMyCollectionsAsync(CancellationToken ct = default)
    {
        var channel = settings().YoutubeChannelId;
        if (string.IsNullOrWhiteSpace(channel)) throw new ServiceException("Enter your YouTube channel ID in settings to list its public playlists.");
        var playlists = new List<CollectionSummary>();
        await foreach (var page in PagesAsync("playlists", new() { ["part"] = "snippet,contentDetails", ["channelId"] = channel, ["maxResults"] = "50" }, ct))
            playlists.AddRange(page.RequiredItems("items", "YouTube").Select(MapSummary));
        playlists.Add(new() { Id = "LL", Name = "Liked Videos", Service = Kind, Kind = CollectionKind.Playlist, Owner = "You", Description = "Private liked videos require YouTube user authorization; API-key access is not supported." });
        playlists.Add(new() { Id = "WL", Name = "Watch Later", Service = Kind, Kind = CollectionKind.Playlist, Owner = "You", Description = "YouTube does not expose Watch Later through its Data API." });
        return playlists;
    }

    public async Task<IReadOnlyList<CollectionSummary>> SearchAsync(string query, CollectionKind kind, int limit = 20, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (kind != CollectionKind.Playlist || limit <= 0 || string.IsNullOrWhiteSpace(query)) return [];
        var found = new List<CollectionSummary>();
        // Preserve the legacy preference for official channels within an oversampled result window.
        var target = (int)Math.Min((long)limit * 2, int.MaxValue);
        await foreach (var page in PagesAsync("search", new() { ["part"] = "snippet", ["q"] = query, ["type"] = "playlist", ["maxResults"] = Math.Min(target, 50).ToString(CultureInfo.InvariantCulture) }, ct))
        {
            found.AddRange(page.RequiredItems("items", "YouTube").Select(MapSummary));
            if (found.Count >= target) break;
        }
        return found.Take(target).OrderByDescending(x => IsOfficial(x.Owner)).Take(limit).Select(x => x with { Owner = CleanArtist(x.Owner) }).ToArray();
    }

    public async Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default)
    {
        var id = ParsePlaylistId(idOrUrl);
        if (id == "WL") throw new ServiceException("YouTube does not allow Watch Later to be read through its Data API. Copy the videos to a public playlist instead.");
        if (id == "LL") throw new ServiceException("YouTube Liked Videos requires user authorization, which this API-key connection does not provide. Use a public playlist instead.");
        var metadata = await RequestAsync("playlists", new() { ["part"] = "snippet,contentDetails", ["id"] = id }, ct);
        var first = metadata.RequiredItems("items", "YouTube").FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) throw new ServiceException("The YouTube playlist was not found or is private.", HttpStatusCode.NotFound);
        var tracks = new List<Track>();
        await foreach (var page in PagesAsync("playlistItems", new() { ["part"] = "snippet,contentDetails", ["playlistId"] = id, ["maxResults"] = "50" }, ct))
        {
            var items = page.RequiredItems("items", "YouTube").ToArray();
            var ids = items.Select(x => x.Get("contentDetails").Text("videoId")).Where(ServiceHttp.IsId).Distinct().ToArray();
            var details = new Dictionary<string, JsonElement>();
            if (ids.Length > 0)
            {
                var data = await RequestAsync("videos", new() { ["part"] = "contentDetails", ["id"] = string.Join(',', ids) }, ct);
                foreach (var video in data.RequiredItems("items", "YouTube")) details[video.Text("id")] = video;
            }
            foreach (var item in items)
            {
                var videoId = item.Get("contentDetails").Text("videoId");
                var snippet = item.Get("snippet");
                var title = snippet.Text("title");
                if (!ServiceHttp.IsId(videoId) || title is "Deleted video" or "Private video") continue;
                var channel = snippet.Text("videoOwnerChannelTitle");
                if (channel.Length == 0) channel = snippet.Text("channelTitle");
                var (artist, song) = ParseTitle(title, channel);
                var duration = TimeSpan.Zero;
                if (details.TryGetValue(videoId, out var detail))
                {
                    try { duration = XmlConvert.ToTimeSpan(detail.Get("contentDetails").Text("duration")); }
                    catch (FormatException) { /* Duration is optional metadata. */ }
                }
                tracks.Add(new Track
                {
                    Title = song, Artist = artist, Service = Kind, ServiceId = videoId,
                    SourceUrl = $"https://www.youtube.com/watch?v={videoId}", Duration = duration,
                    ThumbnailUrl = Thumbnail(snippet), TrackNumber = snippet.Number("position") + 1
                });
            }
        }
        return new MusicCollection { Summary = MapSummary(first) with { TrackCount = tracks.Count }, Tracks = tracks };
    }

    private async IAsyncEnumerable<JsonElement> PagesAsync(string endpoint, Dictionary<string, string> parameters, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var page = await RequestAsync(endpoint, parameters, ct);
            yield return page;
            var token = page.Text("nextPageToken");
            if (token.Length == 0) yield break;
            if (!seen.Add(token)) throw new ServiceException("YouTube returned a repeated page. Please retry the collection.");
            parameters["pageToken"] = token;
        }
    }

    private async Task<JsonElement> RequestAsync(string endpoint, Dictionary<string, string> parameters, CancellationToken ct)
    {
        if (!IsConfigured) throw new ServiceException("Enter your YouTube API key in settings.");
        parameters["key"] = settings().YoutubeApiKey;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/youtube/v3/{endpoint}?{ServiceHttp.Query(parameters)}");
        return await ServiceHttp.SendAsync(httpClient, request, "YouTube", ct);
    }

    private static CollectionSummary MapSummary(JsonElement item)
    {
        var snippet = item.Get("snippet");
        return new()
        {
            Id = item.Get("id").ValueKind == JsonValueKind.Object ? item.Get("id").Text("playlistId") : item.Text("id"),
            Name = WebUtility.HtmlDecode(snippet.Text("title")), Description = WebUtility.HtmlDecode(snippet.Text("description")),
            Owner = snippet.Text("channelTitle"), ThumbnailUrl = Thumbnail(snippet), Service = ServiceKind.YouTube, Kind = CollectionKind.Playlist,
            TrackCount = item.Get("contentDetails").Get("itemCount").ValueKind == JsonValueKind.Number ? item.Get("contentDetails").Number("itemCount") : null
        };
    }
    private static string Thumbnail(JsonElement snippet) => new[] { "medium", "high", "default" }.Select(size => snippet.Get("thumbnails").Get(size).Text("url")).FirstOrDefault(x => x.Length > 0) ?? "";
    private static string ParsePlaylistId(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host is not ("youtube.com" or "www.youtube.com" or "music.youtube.com" or "m.youtube.com") || uri.AbsolutePath != "/playlist")
                throw new ServiceException("Paste a YouTube or YouTube Music playlist URL.");
            value = ServiceHttp.ParseQuery(uri.Query).GetValueOrDefault("list", "");
        }
        if (!ServiceHttp.IsId(value)) throw new ServiceException("Enter a valid YouTube playlist ID or share URL.");
        return value;
    }
    private static string CleanArtist(string value)
    {
        foreach (var suffix in new[] { " - Topic", " - Auto-generated by YouTube" })
            if (value.EndsWith(suffix, StringComparison.Ordinal)) value = value[..^suffix.Length].Trim();
        return value;
    }
    private static bool IsOfficial(string value) => !value.EndsWith(" - Topic", StringComparison.Ordinal) && !value.Contains("- Auto-generated by YouTube", StringComparison.Ordinal) && !value.Contains("Auto-Generated", StringComparison.Ordinal);
    private static (string, string) ParseTitle(string title, string channel)
    {
        title = CleanTitle(WebUtility.HtmlDecode(title));
        string[] patterns = [@"^(.+?)\s*[-–—]\s*(.+?)$", @"^(.+?)\s+by\s+(.+?)$", @"^(.+?):\s*(.+?)$", @"^(.+?)\s*\|\s*(.+?)$"];
        for (var i = 0; i < patterns.Length; i++)
        {
            var match = Regex.Match(title, patterns[i], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (match.Success) return (CleanArtist(match.Groups[i == 1 ? 2 : 1].Value.Trim()), CleanTitle(match.Groups[i == 1 ? 1 : 2].Value));
        }
        return (CleanArtist(channel), CleanTitle(title));
    }
    private static string CleanTitle(string value) => Regex.Replace(value, @"\s*(\((?:Official[^)]*|Music Video|Lyric Video|Audio)\)|\[Official[^\]]*\])", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
}
