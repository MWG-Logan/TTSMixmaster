# Music services

`ServiceCatalog(settings, httpClient, appPaths, openBrowser)` provides one `IMusicService`
per `ServiceKind`. `settings` may be an `AppSettings` instance or `Func<AppSettings>` so a
settings replacement is reflected without rebuilding the catalog. The caller owns the
`HttpClient`. Construction performs no network I/O and never opens a browser.

All service requests use injected `HttpClient` plus `System.Text.Json`. This keeps the
small read-only API surface directly testable without SDK transport adapters. The only
additional package is `System.Security.Cryptography.ProtectedData`, pinned to `11.0.0`.

## Collection IDs

- Last.fm: `lastfm:top_tracks?period=7day&limit=100&user=NAME`,
  `lastfm:loved_tracks?limit=50&user=NAME`, `lastfm:recent_tracks?limit=50&user=NAME`.
  Values are URI escaped. `LastFmService.CreateCollectionId` validates and encodes
  options. Periods are `overall`, `7day`, `1month`, `3month`, `6month`, `12month`;
  limit caps the total tracks, independently of pagination. Legacy bare IDs use
  `overall` and 50 tracks with the configured username. Standard Last.fm user URLs
  ending in `/library/tracks`, `/loved`, and `/library` are accepted; named
  `date_preset` periods map to API periods. Arbitrary custom date ranges are not supported.
- Last.fm albums: `lastfm:album?artist=ARTIST&album=NAME` (URI escaped; build it with
  `LastFmService.CreateAlbumId`), or `https://www.last.fm/music/Artist/Album` (`+` is a space,
  `%2B` a plus). Loaded with `album.getInfo`. The id keeps the requested spelling, not Last.fm's
  correction, so a listed album and its loaded copy share one library folder. An album without a
  track list is a `ServiceException`. Tracks have no audio URL; the downloader searches.
  `GetTopAlbumsAsync(period, limit, user)` lists `user.getTopAlbums` for the same periods as
  top tracks; "your collections" adds the all-time top 50, each an `Album`.
- YouTube: raw playlist IDs, or `https://[www.|music.]youtube.com/playlist?list=ID`.
  The legacy `LL` and `WL` entries remain playlists, with availability explained in
  their descriptions and in errors when selected. Playlists whose ID starts with
  `OLAK5uy_` are YouTube Music albums: kind `Album`, a leading `Album - ` (any case)
  dropped from the name, and the owner taken from the channel when it is an artist
  (`<Artist> - Topic`, suffix stripped), else from the tracks' Topic channel on load,
  never "YouTube". Album tracks keep their title unsplit and carry the album name.
  `https://music.youtube.com/browse/MPREb_…` album pages fail with a message to copy the
  Share link instead: the Data API can't map a browse ID to its playlist without scraping.
- Spotify: raw playlist IDs, `spotify:playlist:ID`, or
  `https://open.spotify.com/playlist/ID` (including localized `/intl-xx/` URLs).
  Albums are `spotify:album:ID` or `https://open.spotify.com/album/ID` (also `/intl-xx/`),
  loaded from `/albums/{id}` plus every `/albums/{id}/tracks` page, in disc then track order.
  `TrackNumber` runs across discs, and the largest album image is the thumbnail.
  Personal collections use `spotify:liked`, `spotify:recent`, and
  `spotify:top:short_term|medium_term|long_term`; signed in, the listing also includes saved
  albums (`/me/albums`).

Last.fm top/loved/recent and Spotify top/recent are `Chart`; albums are `Album`; other
collections are `Playlist`. An album's artist (all artists, comma separated) is its `Owner`.

Search by kind:
- Spotify: `Playlist` (`type=playlist`) and `Album` (`type=album`).
- YouTube: `Playlist` leaves out album playlists; `Album` is the same playlist search keeping
  only `OLAK5uy_` results. The Data API has no album search, so this finds only albums that
  playlist search returns. Both read at most two search pages and return what they found by
  then, even when filtering left fewer than the limit: `search.list` is the most expensive call
  in the Data API quota (the default daily quota allows about 100 searches; see
  [quota costs](https://developers.google.com/youtube/v3/determine_quota_cost)), and "All" runs
  both searches.
- Last.fm: `Album` (`album.search`); there are no playlists.
- `Chart` search returns an empty list everywhere.

These IDs and behavior are also documented in the implementations' XML comments.

## Spotify sign-in

Pass a `Func<Uri, Task>` that opens the system browser. Core does not launch processes
or depend on a UI framework. The first operation needing authentication performs
[Authorization Code with PKCE](https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow).
The configured redirect must use an explicit loopback IP, such as
`http://127.0.0.1:8888/callback`, and match the Spotify app registration. The loopback
listener validates the callback path and random state, handles cancellation, and
times out after three minutes. PKCE mode requires only the client ID; the configured
client secret is used only when `SpotifyUseOauth` is false (public-data mode).

The requested scopes include `user-library-read` (liked songs and saved albums). When a
refresh response lists its scopes and one we request is missing (a sign-in from before a
scope was added), the saved sign-in is cleared and the user is told to connect again, which
prompts a new consent. A 403 on saved albums does the same.

Only the refresh token and its client ID are persisted, as a binary DPAPI CurrentUser
encrypted payload in `AppPaths.DataRoot/spotify_token.json`. Despite the compatibility
filename, this file is not plaintext JSON. Access tokens stay in memory. Tokens and
raw API errors are never logged or included in exception messages. Tokens from the
Python plaintext cache are deliberately not imported. Refreshes preserve an omitted
refresh token and save a rotated token; revoked refresh tokens are cleared so the next
connection can sign in again. A corrupt/unreadable cache returns a recovery message.
Changing client credentials or authentication mode invalidates the cached access token.

The playlist loader uses the current
[`/playlists/{id}/items` endpoint](https://developer.spotify.com/documentation/web-api/reference/get-playlists-items)
and accepts both the current `item` and older `track` payload fields. Spotify API
permissions and development-mode restrictions can limit access to playlists owned by
or shared collaboratively with the signed-in user; such rejections are surfaced to
the user. No account credentials or real service calls are used by the test suite.

## Intentional differences from Python

- Network calls are asynchronous and cancellable. API errors fail the whole operation
  with `ServiceException` instead of printing secrets, swallowing errors, or returning
  partial playlists as successes. `TestConnectionAsync` returns a safe message on
  failure; caller cancellation remains cancellation.
- Listing, searching, and loading follow every API continuation page (search stops
  at its requested limit, YouTube search also after two pages; Last.fm stops at its chosen
  chart limit). Repeated page
  tokens fail rather than looping. Spotify pagination URLs must stay on its API host.
- Last.fm uses HTTPS. The current-playing scrobble remains excluded. The legacy
  track search/detail API is outside `IMusicService` and is not ported.
- Last.fm and Spotify track web pages are not direct audio sources, so their
  `Track.SourceUrl` is empty, as required by the Core contract. YouTube keeps direct
  watch URLs. Spotify durations retain millisecond precision.
- YouTube remains API-key/public-playlist only. Liked Videos needs user OAuth, which
  is outside the existing settings/port scope. Watch Later is
  [unavailable through the Data API](https://developers.google.com/youtube/v3/docs/playlistItems/list).
  Selecting either explains this limitation rather than pretending it loaded.
- YouTube durations are fetched in one batch per playlist page, with no per-track
  searches. Connection checking uses a low-quota `videos.list` call. Deleted/private
  placeholder videos are omitted. Title cleanup preserves meaningful parenthetical
  names such as remixes while removing known promotional suffixes.
- Spotify OAuth no longer silently falls back to public-data mode. User sign-in
  exposes liked songs and top/recent charts (the legacy implementation requested the
  scopes but did not implement those collection loaders). Local Spotify files and
  podcast episodes are omitted; duplicates and playlist order are retained. Recent
  history is limited to what Spotify exposes, not an indefinite listening archive.

## Verification

Service tests use fake JSON/HTTP, temporary app-data roots, and local-only HTTP
callbacks. They cover collection kinds, pagination, share URLs, service errors,
PKCE/state validation, cancellation, DPAPI storage, refresh rotation, and refresh
reuse without opening a browser. The DPAPI/OAuth storage tests require Windows,
matching the application's supported platform.

Build and test the Core projects from the repository root:

```powershell
dotnet build src/TTSMixmaster.Core
dotnet build tests/TTSMixmaster.Core.Tests
dotnet test tests/TTSMixmaster.Core.Tests
```

The coordinator owns the full WinUI solution build after integration; worker
worktree App/XAML build permissions are not part of this service change.
