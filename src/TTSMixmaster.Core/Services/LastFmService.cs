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

    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        try { await RequestAsync("user.getinfo", new() { ["user"] = settings().LastfmUsername }, ct); return null; }
        catch (ServiceException ex) { return ex.Message; }
    }

    public Task<IReadOnlyList<CollectionSummary>> GetMyCollectionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) throw new ServiceException("Enter your Last.fm API key and username in settings.");
        return Task.FromResult<IReadOnlyList<CollectionSummary>>([Summary("top_tracks", "overall", 50, settings().LastfmUsername), Summary("loved_tracks", "overall", 50, settings().LastfmUsername), Summary("recent_tracks", "overall", 50, settings().LastfmUsername)]);
    }

    public Task<IReadOnlyList<CollectionSummary>> SearchAsync(string query, CollectionKind kind, int limit = 20, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CollectionSummary>>([]);
    }

    public async Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default)
    {
        var (collection, options) = ParseId(idOrUrl);
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
            var items = root.Get("track");
            // Last.fm sometimes returns a single object instead of an array.
            var entries = items.ValueKind == JsonValueKind.Object ? new[] { items } : items.Items();
            foreach (var item in entries)
            {
                if (item.Get("@attr").Text("nowplaying") == "true") continue;
                tracks.Add(new Track
                {
                    Title = item.Text("name"), Artist = Name(item.Get("artist")), Album = Name(item.Get("album")),
                    Service = Kind, ServiceId = item.Text("mbid"), PlayCount = item.Number("playcount"),
                    Duration = TimeSpan.FromSeconds(Math.Max(0, item.Number("duration"))),
                    ThumbnailUrl = item.Get("image").Items().Select(x => x.Text("#text")).LastOrDefault(x => x.Length > 0) ?? "",
                    TrackNumber = tracks.Count + 1
                });
                if (tracks.Count == limit) break;
            }
            if (page >= Math.Max(1, root.Get("@attr").Number("totalPages"))) break;
        }
        return new MusicCollection { Summary = Summary(collection, period, limit, user) with { TrackCount = tracks.Count }, Tracks = tracks };
    }

    private async Task<JsonElement> RequestAsync(string method, Dictionary<string, string> parameters, CancellationToken ct)
    {
        var key = settings().LastfmApiKey;
        if (string.IsNullOrWhiteSpace(key)) throw new ServiceException("Enter your Last.fm API key in settings.");
        if (string.IsNullOrWhiteSpace(parameters.GetValueOrDefault("user"))) throw new ServiceException("Enter your Last.fm username in settings.");
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
            if (uri.Scheme != "https" || uri.Host is not ("last.fm" or "www.last.fm")) throw new ServiceException("Paste a Last.fm user chart URL or choose a chart from the list.");
            var parts = uri.AbsolutePath.Trim('/').Split('/');
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
