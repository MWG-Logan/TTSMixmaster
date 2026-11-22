using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Services;

/// <summary>
/// Public YouTube playlists using an API key. IDs are raw playlist IDs, including legacy LL and WL entries.
/// YouTube Music albums are auto-generated playlists whose IDs start with <c>OLAK5uy_</c>; they are
/// <see cref="CollectionKind.Album"/>. The Data API has no album search, so album search is a playlist
/// search that keeps only those playlists. <c>music.youtube.com/browse/MPREb_…</c> album pages can't be
/// mapped to a playlist through the Data API and are rejected with a message pointing at the Share link.
/// </summary>
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
        if (kind is not (CollectionKind.Playlist or CollectionKind.Album) || limit <= 0 || string.IsNullOrWhiteSpace(query)) return [];
        if (kind == CollectionKind.Album) return await SearchAlbumsAsync(query, limit, ct);
        var found = new List<CollectionSummary>();
        // Preserve the legacy preference for official channels within an oversampled result window.
        var target = (int)Math.Min((long)limit * 2, int.MaxValue);
        await foreach (var page in PagesAsync("search", new() { ["part"] = "snippet", ["q"] = query, ["type"] = "playlist", ["maxResults"] = Math.Min(target, 50).ToString(CultureInfo.InvariantCulture) }, ct))
        {
            // Album playlists belong to the Album search, so one query in "All" doesn't list them twice.
            found.AddRange(page.RequiredItems("items", "YouTube").Select(MapSummary).Where(x => x.Kind == CollectionKind.Playlist));
            if (found.Count >= target) break;
        }
        return found.Take(target).OrderByDescending(x => IsOfficial(x.Owner)).Take(limit).Select(x => x with { Owner = CleanArtist(x.Owner) }).ToArray();
    }

    /// <summary>
    /// The Data API has no album search: search playlists and keep the YouTube Music album playlists. Each search
    /// page costs 100 quota units, so this reads at most <see cref="AlbumSearchPages"/> pages even when few match.
    /// </summary>
    private async Task<IReadOnlyList<CollectionSummary>> SearchAlbumsAsync(string query, int limit, CancellationToken ct)
    {
        var found = new List<CollectionSummary>();
        var pages = 0;
        await foreach (var page in PagesAsync("search", new() { ["part"] = "snippet", ["q"] = query, ["type"] = "playlist", ["maxResults"] = "50" }, ct))
        {
            found.AddRange(page.RequiredItems("items", "YouTube").Select(MapSummary).Where(x => x.Kind == CollectionKind.Album));
            if (found.Count >= limit || ++pages >= AlbumSearchPages) break;
        }
        return found.Take(limit).ToArray();
    }

    private const int AlbumSearchPages = 2;
    private const string AlbumPrefix = "OLAK5uy_";

    public async Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default)
    {
        var id = ParsePlaylistId(idOrUrl);
        if (id == "WL") throw new ServiceException("YouTube does not allow Watch Later to be read through its Data API. Copy the videos to a public playlist instead.");
        if (id == "LL") throw new ServiceException("YouTube Liked Videos requires user authorization, which this API-key connection does not provide. Use a public playlist instead.");
        var metadata = await RequestAsync("playlists", new() { ["part"] = "snippet,contentDetails", ["id"] = id }, ct);
        var first = metadata.RequiredItems("items", "YouTube").FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) throw new ServiceException("The YouTube playlist was not found or is private.", HttpStatusCode.NotFound);
        var summary = MapSummary(first);
        var isAlbum = summary.Kind == CollectionKind.Album;
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
                // Album tracks come from the artist's "<Artist> - Topic" channel and are titled with the song alone,
                // so "Song - 2011 Remaster" must not be split into an artist and a title.
                var (artist, song) = isAlbum && IsTopic(channel) ? (CleanArtist(channel), CleanTitle(WebUtility.HtmlDecode(title))) : ParseTitle(title, channel);
                var duration = TimeSpan.Zero;
                if (details.TryGetValue(videoId, out var detail))
                {
                    try { duration = XmlConvert.ToTimeSpan(detail.Get("contentDetails").Text("duration")); }
                    catch (FormatException) { /* Duration is optional metadata. */ }
                }
                tracks.Add(new Track
                {
                    Title = song, Artist = artist, Album = isAlbum ? summary.Name : "", Service = Kind, ServiceId = videoId,
                    SourceUrl = $"https://www.youtube.com/watch?v={videoId}", Duration = duration,
                    ThumbnailUrl = Thumbnail(snippet), TrackNumber = snippet.Number("position") + 1
                });
            }
        }
        // Album playlists are owned by "YouTube"; the artist's Topic channel on the tracks names the album's artist.
        if (isAlbum && summary.Owner.Length == 0)
            summary = summary with { Owner = tracks.GroupBy(x => x.Artist).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "" };
        return new MusicCollection { Summary = summary with { TrackCount = tracks.Count }, Tracks = tracks };
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
        var id = item.Get("id").ValueKind == JsonValueKind.Object ? item.Get("id").Text("playlistId") : item.Text("id");
        var name = WebUtility.HtmlDecode(snippet.Text("title"));
        var owner = snippet.Text("channelTitle");
        var isAlbum = id.StartsWith(AlbumPrefix, StringComparison.Ordinal);
        if (isAlbum)
        {
            name = AlbumName(name);
            // "YouTube" (or "YouTube Music") owns every generated album; only an artist channel says something useful.
            owner = owner is "YouTube" or "YouTube Music" ? "" : CleanArtist(owner);
        }
        return new()
        {
            Id = id, Name = name, Description = WebUtility.HtmlDecode(snippet.Text("description")),
            Owner = owner, ThumbnailUrl = Thumbnail(snippet), Service = ServiceKind.YouTube, Kind = isAlbum ? CollectionKind.Album : CollectionKind.Playlist,
            TrackCount = item.Get("contentDetails").Get("itemCount").ValueKind == JsonValueKind.Number ? item.Get("contentDetails").Number("itemCount") : null
        };
    }

    /// <summary>YouTube Music titles album playlists "Album - Name"; the prefix is dropped (any case).</summary>
    internal static string AlbumName(string title) =>
        title.StartsWith("Album - ", StringComparison.OrdinalIgnoreCase) && title.Length > 8 ? title[8..].Trim() : title;
    private static string Thumbnail(JsonElement snippet) => new[] { "medium", "high", "default" }.Select(size => snippet.Get("thumbnails").Get(size).Text("url")).FirstOrDefault(x => x.Length > 0) ?? "";
    private static string ParsePlaylistId(string value)
    {
        value = value.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == "https" && uri.Host == "music.youtube.com" && uri.AbsolutePath.StartsWith("/browse/MPREb_", StringComparison.Ordinal))
                throw new ServiceException("This YouTube Music album page can't be opened directly. Open the album on YouTube Music and copy the link from Share, which gives a playlist link.");
            if (uri.Scheme != "https" || uri.Host is not ("youtube.com" or "www.youtube.com" or "music.youtube.com" or "m.youtube.com") || uri.AbsolutePath != "/playlist")
                throw new ServiceException("Paste a YouTube or YouTube Music playlist or album URL.");
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
    private static bool IsTopic(string channel) => channel.EndsWith(" - Topic", StringComparison.Ordinal);
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
