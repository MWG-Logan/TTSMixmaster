using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TTSMixmaster.Core.Configuration;
using TTSMixmaster.Core.Models;

namespace TTSMixmaster.Core.Services;

/// <summary>
/// Spotify metadata client with lazy Authorization Code + PKCE sign-in and optional public client-credentials mode.
/// Playlist IDs are raw Spotify IDs; spotify:playlist:ID and https://open.spotify.com/playlist/ID are accepted.
/// Personal IDs: spotify:liked, spotify:recent, spotify:top:short_term, spotify:top:medium_term, spotify:top:long_term.
/// Recent history and top charts include all pages exposed by Spotify; history is not a complete listening archive.
/// </summary>
public sealed class SpotifyService : IMusicService
{
    private readonly Func<AppSettings> _settings;
    private readonly HttpClient _httpClient;
    private readonly SpotifyAuthentication _authentication;

    public SpotifyService(AppSettings settings, HttpClient httpClient, AppPaths paths, Func<Uri, Task>? openBrowser = null)
        : this(() => settings, httpClient, paths, openBrowser) { }

    /// <param name="openBrowser">Opens the system browser without a Core UI dependency. Called only when no refresh token is available.</param>
    public SpotifyService(Func<AppSettings> settings, HttpClient httpClient, AppPaths paths, Func<Uri, Task>? openBrowser = null)
    {
        _settings = settings;
        _httpClient = httpClient;
        _authentication = new(settings, httpClient, new SpotifyTokenStore(paths), openBrowser);
    }

    public ServiceKind Kind => ServiceKind.Spotify;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings().SpotifyClientId) && (_settings().SpotifyUseOauth || !string.IsNullOrWhiteSpace(_settings().SpotifyClientSecret));

    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await RequestAsync(_settings().SpotifyUseOauth ? "me" : "search?type=playlist&q=test&limit=1", ct);
            return null;
        }
        catch (ServiceException ex) { return ex.Message; }
    }

    public async Task<IReadOnlyList<CollectionSummary>> GetMyCollectionsAsync(CancellationToken ct = default)
    {
        var options = _settings();
        if (!options.SpotifyUseOauth && string.IsNullOrWhiteSpace(options.SpotifyUserId)) throw new ServiceException("Enter a Spotify user ID for public-data mode, or enable user sign-in.");
        var endpoint = options.SpotifyUseOauth ? "me/playlists?limit=50" : $"users/{Uri.EscapeDataString(options.SpotifyUserId)}/playlists?limit=50";
        var collections = new List<CollectionSummary>();
        await foreach (var page in PagesAsync(endpoint, ct))
            collections.AddRange(page.RequiredItems("items", "Spotify").Where(x => x.ValueKind == JsonValueKind.Object).Select(MapSummary));
        if (options.SpotifyUseOauth)
        {
            collections.Add(PersonalSummary("spotify:liked"));
            collections.Add(PersonalSummary("spotify:recent"));
            foreach (var period in new[] { "short_term", "medium_term", "long_term" }) collections.Add(PersonalSummary($"spotify:top:{period}"));
        }
        return collections;
    }

    public async Task<IReadOnlyList<CollectionSummary>> SearchAsync(string query, CollectionKind kind, int limit = 20, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (kind != CollectionKind.Playlist || limit <= 0 || string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<CollectionSummary>();
        // Ten is accepted in current development mode as well as extended-quota applications.
        var endpoint = $"search?type=playlist&q={Uri.EscapeDataString(query)}&limit={Math.Min(limit, 10)}";
        await foreach (var page in PagesAsync(endpoint, ct, "playlists"))
        {
            results.AddRange(page.RequiredItems("items", "Spotify").Where(x => x.ValueKind == JsonValueKind.Object).Select(MapSummary));
            if (results.Count >= limit) break;
        }
        return results.Take(limit).ToArray();
    }

    public async Task<MusicCollection> GetCollectionAsync(string idOrUrl, CancellationToken ct = default)
    {
        var id = ParseId(idOrUrl);
        CollectionSummary summary;
        string endpoint;
        var isTop = id.StartsWith("spotify:top:", StringComparison.Ordinal);
        if (id is "spotify:liked" or "spotify:recent" || isTop)
        {
            if (!_settings().SpotifyUseOauth) throw new ServiceException("Enable Spotify user sign-in to access liked songs, top tracks or listening history.");
            summary = PersonalSummary(id);
            endpoint = id switch
            {
                "spotify:liked" => "me/tracks?limit=50",
                "spotify:recent" => "me/player/recently-played?limit=50",
                _ => $"me/top/tracks?time_range={id[12..]}&limit=50"
            };
        }
        else
        {
            summary = MapSummary(await RequestAsync($"playlists/{id}", ct));
            if (summary.Id.Length == 0) throw new ServiceException("Spotify returned incomplete playlist information.");
            endpoint = $"playlists/{id}/items?limit=50";
        }
        var tracks = new List<Track>();
        var position = 0;
        await foreach (var page in PagesAsync(endpoint, ct))
        {
            foreach (var item in page.RequiredItems("items", "Spotify"))
            {
                position++;
                var track = isTop ? item : item.Get("track");
                // Spotify renamed the playlist-item field; also accept older/extended-quota payloads.
                if (!isTop && track.ValueKind == JsonValueKind.Undefined) track = item.Get("item");
                if (track.ValueKind != JsonValueKind.Object || track.Text("type") != "track" || track.Get("is_local").ValueKind == JsonValueKind.True) continue;
                tracks.Add(new Track
                {
                    Title = track.Text("name"), Artist = string.Join(", ", track.Get("artists").Items().Select(a => a.Text("name"))),
                    Album = track.Get("album").Text("name"), Service = Kind, ServiceId = track.Text("id"),
                    Duration = TimeSpan.FromMilliseconds(Math.Max(0, track.Number("duration_ms"))),
                    ThumbnailUrl = track.Get("album").Get("images").Items().Select(x => x.Text("url")).FirstOrDefault() ?? "",
                    TrackNumber = position
                });
            }
        }
        return new MusicCollection { Summary = summary with { TrackCount = tracks.Count }, Tracks = tracks };
    }

    private async IAsyncEnumerable<JsonElement> PagesAsync(string endpoint, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, string? wrapper = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (endpoint.Length > 0)
        {
            if (!seen.Add(endpoint)) throw new ServiceException("Spotify returned a repeated page. Please retry the collection.");
            var data = await RequestAsync(endpoint, ct);
            var page = wrapper is null ? data : data.Get(wrapper);
            yield return page;
            endpoint = page.Text("next");
        }
    }

    private async Task<JsonElement> RequestAsync(string endpoint, CancellationToken ct)
    {
        if (!Uri.TryCreate(new Uri("https://api.spotify.com/v1/"), endpoint, out var uri))
            throw new ServiceException("Spotify returned an invalid pagination URL.");
        // Never send a bearer token to an arbitrary pagination URL returned by a server.
        if (uri.Scheme != "https" || uri.Host != "api.spotify.com" || !uri.IsDefaultPort || !uri.AbsolutePath.StartsWith("/v1/", StringComparison.Ordinal) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new ServiceException("Spotify returned an invalid pagination URL.");
        var token = await _authentication.GetTokenAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            try { return await ServiceHttp.SendAsync(_httpClient, request, "Spotify", ct); }
            catch (ServiceException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            { token = await _authentication.GetTokenAsync(ct, token); }
        }
    }

    private static CollectionSummary MapSummary(JsonElement item) => new()
    {
        Id = item.Text("id"), Name = item.Text("name"), Description = WebUtility.HtmlDecode(item.Text("description")),
        Owner = item.Get("owner").Text("display_name") is { Length: > 0 } owner ? owner : item.Get("owner").Text("id"),
        Service = ServiceKind.Spotify, Kind = CollectionKind.Playlist,
        TrackCount = item.Get("tracks").ValueKind == JsonValueKind.Object ? item.Get("tracks").Number("total") : item.Get("items").ValueKind == JsonValueKind.Object ? item.Get("items").Number("total") : null,
        ThumbnailUrl = item.Get("images").Items().Select(x => x.Text("url")).FirstOrDefault() ?? ""
    };

    private static CollectionSummary PersonalSummary(string id) => new()
    {
        Id = id, Service = ServiceKind.Spotify, Kind = id == "spotify:liked" ? CollectionKind.Playlist : CollectionKind.Chart, Owner = "You",
        Name = id switch { "spotify:liked" => "Liked Songs", "spotify:recent" => "Recently Played", "spotify:top:short_term" => "Top Tracks (4 weeks)", "spotify:top:medium_term" => "Top Tracks (6 months)", _ => "Top Tracks (long term)" }
    };

    private static string ParseId(string value)
    {
        value = value.Trim();
        if (value is "spotify:liked" or "spotify:recent" or "spotify:top:short_term" or "spotify:top:medium_term" or "spotify:top:long_term") return value;
        if (value.StartsWith("spotify:playlist:", StringComparison.Ordinal)) value = value[17..];
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 3 && parts[0].StartsWith("intl-", StringComparison.Ordinal)) parts = parts[1..];
            if (uri.Scheme != "https" || uri.Host != "open.spotify.com" || parts.Length != 2 || parts[0] != "playlist")
                throw new ServiceException("Paste a Spotify playlist share URL.");
            value = parts[1];
        }
        if (value.Length == 0 || !value.All(char.IsAsciiLetterOrDigit)) throw new ServiceException("Enter a valid Spotify playlist ID or share URL.");
        return value;
    }
}
