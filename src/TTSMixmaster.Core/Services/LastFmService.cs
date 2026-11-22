using System.Globalization;
using System.Text.Json;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Services;

/// <summary>
/// Read-only Last.fm charts. IDs are <c>lastfm:top_tracks?user=NAME&amp;period=overall&amp;limit=50</c>,
/// <c>lastfm:loved_tracks?user=NAME&amp;limit=50</c> or <c>lastfm:recent_tracks?user=NAME&amp;limit=50</c>.
/// Values are URI escaped; user defaults to the configured username. Limit is a total track cap, not a page size.
/// Supported periods: overall, 7day, 1month, 3month, 6month, 12month. Bare legacy IDs use overall/50.
/// Albums are <c>lastfm:album?artist=ARTIST&amp;album=NAME</c> (see <see cref="CreateAlbumId"/>) or a
/// <c>https://www.last.fm/music/Artist/Album</c> page, loaded with album.getInfo. Album tracks have no audio URL.
/// </summary>
public sealed class LastFmService(Func<AppSettings> settings, HttpClient httpClient) : IMusicService
{
    public LastFmService(AppSettings settings, HttpClient httpClient) : this(() => settings, httpClient) { }
    public ServiceKind Kind => ServiceKind.LastFm;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings().LastfmApiKey) && !string.IsNullOrWhiteSpace(settings().LastfmUsername);

    public static string CreateCollectionId(string collection, string period = "overall", int limit = 50, string? username = null)
    {
        Validate(collection, period, limit);
        var options = new Dictionary<string, string> { ["period"] = period, ["limit"] = limit.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(username)) options["user"] = username;
        return $"lastfm:{collection}?{ServiceHttp.Query(options)}";
    }

    /// <summary>The id of one album: artist and album name as Last.fm spells them, URI escaped.</summary>
    public static string CreateAlbumId(string artist, string album)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album)) throw new ServiceException("A Last.fm album needs both an artist and an album name.");
        return "lastfm:album?" + ServiceHttp.Query(new Dictionary<string, string> { ["artist"] = artist, ["album"] = album });
    }

    /// <summary>
    /// The user's most played albums (user.getTopAlbums) for one chart period, most played first.
    /// Each entry is an <see cref="CollectionKind.Album"/> that opens with <see cref="GetCollectionAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<CollectionSummary>> GetTopAlbumsAsync(string period = "overall", int limit = 50, string? username = null, CancellationToken ct = default)
    {
        Validate("top_tracks", period, limit);
        var user = string.IsNullOrWhiteSpace(username) ? settings().LastfmUsername : username;
        var albums = new List<CollectionSummary>();
        var pageSize = Math.Min(limit, 200);
        for (var page = 1; albums.Count < limit; page++)
        {
            var data = await RequestAsync("user.gettopalbums", new() { ["user"] = user, ["period"] = period, ["limit"] = pageSize.ToString(CultureInfo.InvariantCulture), ["page"] = page.ToString(CultureInfo.InvariantCulture) }, ct);
            var root = data.Get("topalbums");
            if (root.ValueKind != JsonValueKind.Object) throw new ServiceException("Last.fm returned an incomplete chart.");
            foreach (var item in Entries(root.Get("album")))
            {
                if (AlbumSummary(item) is not { } album) continue;
                var plays = item.Number("playcount");
                albums.Add(album with { Description = $"#{albums.Count + 1} in {user}'s top albums ({PeriodName(period)})" + (plays > 0 ? $" · {plays:N0} plays" : "") });
                if (albums.Count == limit) break;
            }
            if (page >= Math.Max(1, root.Get("@attr").Number("totalPages"))) break;
        }
        return albums;
    }

    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        try { await RequestAsync("user.getinfo", new() { ["user"] = settings().LastfmUsername }, ct); return null; }
        catch (ServiceException ex) { return ex.Message; }
    }

    /// <summary>The three track charts, then the user's all-time top albums (each openable as an album).</summary>
    public async Task<IReadOnlyList<CollectionSummary>> GetMyCollectionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new ServiceException("Enter your Last.fm API key and username in settings.");
        var user = settings().LastfmUsername;
        return [Summary("top_tracks", "overall", 50, user), Summary("loved_tracks", "overall", 50, user), Summary("recent_tracks", "overall", 50, user), .. await GetTopAlbumsAsync("overall", 50, user, ct)];
    }

    /// <summary>Album search (album.search). Last.fm has no playlists, so other kinds return nothing.</summary>
    public async Task<IReadOnlyList<CollectionSummary>> SearchAsync(string query, CollectionKind kind, int limit = 20, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (kind != CollectionKind.Album || limit <= 0 || string.IsNullOrWhiteSpace(query)) return [];
        var found = new List<CollectionSummary>();
        var pageSize = Math.Min(limit, 50);
        for (var page = 1; found.Count < limit; page++)
        {
            var data = await RequestAsync("album.search", new() { ["album"] = query, ["limit"] = pageSize.ToString(CultureInfo.InvariantCulture), ["page"] = page.ToString(CultureInfo.InvariantCulture) }, ct);
            var results = data.Get("results");
            if (results.ValueKind != JsonValueKind.Object) throw new ServiceException("Last.fm returned incomplete search results.");
            var entries = Entries(results.Get("albummatches").Get("album")).ToArray();
            found.AddRange(entries.Select(AlbumSummary).OfType<CollectionSummary>());
            if (entries.Length < pageSize || page * pageSize >= results.Number("opensearch:totalResults")) break;
        }
        return found.Take(limit).ToArray();
    }

    public async Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default)
    {
        var (collection, options) = ParseId(idOrUrl);
        if (collection == "album") return await GetAlbumAsync(options.GetValueOrDefault("artist", ""), options.GetValueOrDefault("album", ""), ct);
        var period = options.GetValueOrDefault("period", "overall");
        var limit = options.TryGetValue("limit", out var text) ? int.TryParse(text, out var parsed) ? parsed : 0 : 50;
        Validate(collection, period, limit);
        var user = options.GetValueOrDefault("user", settings().LastfmUsername);
        if (string.IsNullOrWhiteSpace(user)) throw new ServiceException("Enter a Last.fm username in settings.");
        var method = collection switch { "top_tracks" => "gettoptracks", "loved_tracks" => "getlovedtracks", _ => "getrecenttracks" };
        var rootName = collection.Replace("_", "");
        var tracks = new List<Track>();
        var pageSize = Math.Min(limit, 200);
        for (var page = 1; tracks.Count < limit; page++)
        {
            var data = await RequestAsync($"user.{method}", new() { ["user"] = user, ["period"] = period, ["limit"] = pageSize.ToString(CultureInfo.InvariantCulture), ["page"] = page.ToString(CultureInfo.InvariantCulture) }, ct);
            var root = data.Get(rootName);
            if (root.ValueKind != JsonValueKind.Object) throw new ServiceException("Last.fm returned an incomplete chart.");
            foreach (var item in Entries(root.Get("track")))
            {
                if (item.Get("@attr").Text("nowplaying") == "true") continue;
                tracks.Add(new Track
                {
                    Title = item.Text("name"), Artist = Name(item.Get("artist")), Album = Name(item.Get("album")),
                    Service = Kind, ServiceId = item.Text("mbid"), PlayCount = item.Number("playcount"),
                    Duration = TimeSpan.FromSeconds(Math.Max(0, item.Number("duration"))),
                    ThumbnailUrl = LargestImage(item),
                    TrackNumber = tracks.Count + 1
                });
                if (tracks.Count == limit) break;
            }
            if (page >= Math.Max(1, root.Get("@attr").Number("totalPages"))) break;
        }
        return new MusicCollection { Summary = Summary(collection, period, limit, user) with { TrackCount = tracks.Count }, Tracks = tracks };
    }

    private async Task<MusicCollection> GetAlbumAsync(string artist, string name, CancellationToken ct)
    {
        var id = CreateAlbumId(artist, name);
        var data = await RequestAsync("album.getinfo", new() { ["artist"] = artist, ["album"] = name }, ct);
        var album = data.Get("album");
        if (album.ValueKind != JsonValueKind.Object) throw new ServiceException("Last.fm returned incomplete album information.");
        var albumName = album.Text("name") is { Length: > 0 } n ? n : name;
        var albumArtist = Name(album.Get("artist")) is { Length: > 0 } a ? a : artist;
        var image = LargestImage(album);
        var tracks = Entries(album.Get("tracks").Get("track")).Select((item, index) => new Track
        {
            Title = item.Text("name"), Artist = Name(item.Get("artist")) is { Length: > 0 } trackArtist ? trackArtist : albumArtist,
            Album = albumName, Service = Kind, ServiceId = item.Text("mbid"),
            Duration = TimeSpan.FromSeconds(Math.Max(0, item.Number("duration"))), ThumbnailUrl = image,
            TrackNumber = item.Get("@attr").Number("rank") is > 0 and var rank ? rank : index + 1
        }).Where(x => x.Title.Length > 0).OrderBy(x => x.TrackNumber).ToList();
        if (tracks.Count == 0) throw new ServiceException("Last.fm has no track list for this album. Try the album on Spotify or YouTube instead.");
        // The id stays as requested (not Last.fm's corrected spelling), so a listed album and its loaded copy share one library folder.
        var summary = new CollectionSummary
        {
            Id = id, Name = albumName, Owner = albumArtist, Service = Kind, Kind = CollectionKind.Album, ThumbnailUrl = image, TrackCount = tracks.Count
        };
        return new MusicCollection { Summary = summary, Tracks = tracks };
    }

    private async Task<JsonElement> RequestAsync(string method, Dictionary<string, string> parameters, CancellationToken ct)
    {
        var key = settings().LastfmApiKey;
        if (string.IsNullOrWhiteSpace(key)) throw new ServiceException("Enter your Last.fm API key in settings.");
        if (method.StartsWith("user.", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(parameters.GetValueOrDefault("user"))) throw new ServiceException("Enter your Last.fm username in settings.");
        parameters["method"] = method; parameters["api_key"] = key; parameters["format"] = "json";
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://ws.audioscrobbler.com/2.0/?" + ServiceHttp.Query(parameters));
        var data = await ServiceHttp.SendAsync(httpClient, request, "Last.fm", ct);
        if (data.Get("error").ValueKind != JsonValueKind.Undefined)
            throw new ServiceException(data.Number("error") switch
            {
                4 or 9 or 10 or 26 => "Last.fm authentication failed. Check your API key.",
                6 => "Last.fm could not find that user or collection.",
                29 => "Last.fm rate limit reached. Please try again later.",
                _ => "Last.fm could not load this collection. Please try again later."
            });
        return data;
    }

    /// <summary>Last.fm returns a single object instead of a one-element array.</summary>
    private static IEnumerable<JsonElement> Entries(JsonElement items) => items.ValueKind == JsonValueKind.Object ? [items] : items.Items();

    /// <summary>Last.fm lists images small to large; the last non-empty one is the largest.</summary>
    /// <summary>The last (largest) image, or empty when there is none or it is Last.fm's grey-star placeholder.</summary>
    internal static string LargestImage(JsonElement item) =>
        item.Get("image").Items().Select(x => x.Text("#text")).LastOrDefault(x => x.Length > 0) is { } url && !url.Contains(PlaceholderImage, StringComparison.OrdinalIgnoreCase) ? url : "";

    /// <summary>The image Last.fm returns for tracks it has no art for (most chart tracks), so it isn't art.</summary>
    private const string PlaceholderImage = "2a96cbd8b46e442fc41c2b86b821562f";

    private static CollectionSummary? AlbumSummary(JsonElement item)
    {
        var name = item.Text("name");
        var artist = Name(item.Get("artist"));
        if (name.Length == 0 || artist.Length == 0) return null;
        return new() { Id = CreateAlbumId(artist, name), Name = name, Owner = artist, Service = ServiceKind.LastFm, Kind = CollectionKind.Album, ThumbnailUrl = LargestImage(item) };
    }

    private static string PeriodName(string period) => period switch
    {
        "7day" => "last 7 days", "1month" => "last month", "3month" => "last 3 months", "6month" => "last 6 months", "12month" => "last year", _ => "all time"
    };

    private static string Name(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value.Text("name") is { Length: > 0 } name ? name : value.Text("#text") : value.Text();
    private static void Validate(string collection, string period, int limit)
    {
        if (collection is not ("top_tracks" or "loved_tracks" or "recent_tracks")) throw new ServiceException("Choose a Last.fm Top Tracks, Loved Tracks or Recent Tracks chart.");
        if (period is not ("overall" or "7day" or "1month" or "3month" or "6month" or "12month") || limit < 1)
            throw new ServiceException("Choose a valid Last.fm period and a positive track limit.");
    }
    private static CollectionSummary Summary(string collection, string period, int limit, string user) => new()
    {
        Id = CreateCollectionId(collection, period, limit, user), Service = ServiceKind.LastFm, Kind = CollectionKind.Chart, Owner = user,
        Name = collection switch { "top_tracks" => $"Top Tracks ({period})", "loved_tracks" => "Loved Tracks", _ => "Recent Tracks" },
        Description = $"Up to {limit} tracks from {user}'s Last.fm chart."
    };
    private static (string, Dictionary<string, string>) ParseId(string value)
    {
        value = value.Trim();
        if (value.StartsWith("lastfm:", StringComparison.Ordinal)) value = value[7..];
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host is not ("last.fm" or "www.last.fm")) throw new ServiceException("Paste a Last.fm user chart or album URL, or choose one from the list.");
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 3 && parts[0] == "music")
            {
                // /music/Artist/Album: '+' is a space in Last.fm paths, a literal plus is %2B. /music/Artist/_/Track and /music/Artist/+tag pages aren't albums.
                if (parts[2] == "_" || parts[2].StartsWith('+')) throw new ServiceException("That Last.fm URL is not an album page.");
                var segment = (string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
                return ("album", new(StringComparer.Ordinal) { ["artist"] = segment(parts[1]), ["album"] = segment(parts[2]) });
            }
            if (parts.Length < 3 || parts[0] != "user") throw new ServiceException("That Last.fm URL does not identify a user chart.");
            var collection = string.Join('/', parts.Skip(2)) switch { "loved" => "loved_tracks", "library/tracks" => "top_tracks", "library" => "recent_tracks", _ => "" };
            var options = ServiceHttp.ParseQuery(uri.Query);
            options["user"] = Uri.UnescapeDataString(parts[1]);
            if (options.TryGetValue("date_preset", out var preset)) options["period"] = preset switch
            {
                "ALL" => "overall", "LAST_7_DAYS" => "7day", "LAST_30_DAYS" => "1month", "LAST_90_DAYS" => "3month", "LAST_180_DAYS" => "6month", "LAST_365_DAYS" => "12month", _ => "invalid"
            };
            return (collection, options);
        }
        var pair = value.Split('?', 2);
        return (pair[0], ServiceHttp.ParseQuery(pair.Length == 2 ? pair[1] : ""));
    }
}
