# Live checklist (guided run with the owner)

What the unattended Sandbox run couldn't cover: interactive Azure sign-in (WAM), real music-service
accounts, real Azure uploads and Tabletop Simulator itself. Run it together, on the owner's machine,
with a published or installed build (not `dotnet run`). About 30–45 minutes.

Before starting: note the app version (Settings > About), and keep `%LOCALAPPDATA%\TTSMixmaster\logs`
open; every step below should leave a line there and none should contain a key, secret or token.

## 1. First run and settings import

- [ ] With no `%LOCALAPPDATA%\TTSMixmaster\config.json`, put the old Python `config.json` next to the exe (or in the
      working directory) and start the app. Settings shows **Settings imported from the previous version**, and
      the services and connection string from the old file are filled in.
- [ ] Restart: the notice doesn't come back, and the old file is not modified.
- [ ] If the installed build lacks yt-dlp/ffmpeg, Collections shows **Builds can't find audio yet**. An installed
      release should not show it.

## 2. Music services (real accounts)

For each: open its expander in Settings, check the status reads **Set up, not tested**, press **Test connection**.

- [ ] Last.fm: succeeds with the real key and username; a wrong key gives a readable error (no raw API text).
- [ ] YouTube: succeeds with the real key; with a channel ID, Collections lists the owner's playlists.
- [ ] Spotify, **Sign in with Spotify** on: Test opens the browser once; after approving, it succeeds. Restart the
      app: Liked Songs / Top tracks load without signing in again (refresh token in `spotify_token.json`, DPAPI).
- [ ] Collections, "All connected services": each service's collections appear; a pasted Spotify/YouTube link opens.

## 3. Azure sign-in and the storage picker (WAM)

- [ ] **Sign in to Azure** shows the Windows account picker (WAM) over the app window, not a browser tab.
      Signed in, the Account row shows the account name and **Sign out**.
- [ ] Directory (only if more than one) → Subscription → Storage account → Container cascade: placeholders read
      "Choose X first" / "Loading…", single items are picked automatically.
- [ ] Choosing a container saves `azure_tenant_id`, `azure_subscription_id`, `azure_storage_account`,
      `azure_container_name` in config.json. Restart: the app is still signed in and the same container is preselected.
- [ ] A container where the owner lacks a data role: **Grant me access** assigns Storage Blob Data Contributor at
      that container; the "waiting for Azure" bar shows until access appears (up to 2 minutes), then the next state.
      Check the role assignment in the portal: scope is the container only.
- [ ] A private container: **Allow public read access** asks first (Cancel is the default), then sets the
      account's public access and the container to Blob. Check in the portal that nothing else changed.
- [ ] **New container** creates a private container and selects it.
- [ ] Storage **Test connection**: succeeds; on an empty container it notes anonymous read couldn't be verified yet.
- [ ] **Sign out**, then build: the Collection page says to sign in first. Sign in again: everything resumes.

## 4. Build with real services and real Azure

- [ ] Open a small real playlist (5–10 tracks). The chip preview shows the playlist's art and title; toggling
      **Show title on chip** and choosing a custom image re-render it; both choices survive a restart.
- [ ] **Build for Tabletop Simulator**: per-track status updates live, "Step 2 of 3 · Uploading to Azure · n of m".
- [ ] In the container: `audio/<sha256>.mp3` blobs and one `images/chip-<sha256>.png`. Open a blob URL in a private
      browser window: it plays/loads without signing in.
- [ ] **Build again** without changes: tracks show "Already downloaded" / "Already uploaded"; no new blobs.
- [ ] **Cancel build** mid-download: "Build cancelled", nothing generated, downloaded files kept.

## 5. Tabletop Simulator

- [ ] **Export to TTS**: `Documents\My Games\Tabletop Simulator\Saves\Saved Objects\TTSMixmaster\<name>.json` and a
      256×256 `.png` that is the chip.
- [ ] In TTS: Objects → Saved Objects → TTSMixmaster shows the chip thumbnail; place it. The chip front is the
      rendered chip, the back is the standard MWG back, and the tracks play from Azure.
- [ ] Edit the saved `.json` in a text editor, export again: **Replace the saved object?** appears; Keep both writes
      `<name> (2).json`.
- [ ] Builds page: the build shows "Exported to TTS <time>"; **Export to TTS** there works too.

## 6. Tools

- [ ] Settings > **Update yt-dlp** (with internet): succeeds and names the version; the file lands in
      `%LOCALAPPDATA%\TTSMixmaster\tools\yt-dlp.exe`, and the install folder is unchanged.

## 7. Albums

These need the owner's own accounts. The unit and App tests cover the same paths with recorded responses.

- [ ] **Spotify saved albums:** signed in, **Collections → Albums** lists your saved albums with their artist
      ("Spotify · Album · n tracks · Artist"). If Spotify hadn't granted saved-album access to an older sign-in,
      the page says to connect again: **Sign in with Spotify**, approve, and the albums appear.
- [ ] **Spotify album link:** paste `https://open.spotify.com/album/…` (also try an `/intl-xx/` link). The album
      opens with its tracks in order (a multi-disc album keeps numbering on disc 2), and the chip shows the cover.
- [ ] **Spotify album search:** on **Albums**, search an album name: albums only. Under **All**, the same search
      shows playlists and albums, each labelled.
- [ ] **YouTube Music album link:** on music.youtube.com open an album, **Share → Copy link**
      (`music.youtube.com/playlist?list=OLAK5uy_…`) and paste it. It opens as an album named without
      "Album - ", with the artist (not "YouTube") in the header and the album's song titles.
- [ ] **YouTube Music browse link:** paste the address-bar link `music.youtube.com/browse/MPREb_…`: the error
      says to copy the link from Share.
- [ ] **YouTube album search:** on **Albums**, search "<artist> <album>". Only album playlists appear, possibly
      none (YouTube's API has no album search).
- [ ] **Last.fm album:** paste `https://www.last.fm/music/<Artist>/<Album>`, and search an album on **Albums**.
      It opens with its track list. Your top albums appear under **Albums** in your own collections.
- [ ] **Build** one album from each service: tracks download (Last.fm and Spotify by search), upload and export
      like a playlist. Building a playlist that shares a track with the album leaves the album's folder alone.

Record anything off-script as a GitHub issue (see `docs/agents/issue-tracker.md`).
