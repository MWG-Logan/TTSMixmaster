# Usage Guide

## Running the app

Start **TTSMixmaster** from the Start menu. With the portable zip, run `TTSMixmaster.exe` from the
extracted folder. See the [Installation Guide](INSTALLATION.md) to install it.

The app has three places, in the navigation pane: **Collections**, **Builds** and **Settings**.

## 1. Connect your music services (Settings)

Open **Settings → Music services** and fill in the services you use. All of them are optional.

- **Last.fm:** API key, shared secret and username, for your top, loved and recent tracks (charts) and
  your top albums.
- **YouTube:** an API key, and a channel ID for your own playlists.
- **Spotify:** a client ID (and redirect URI), then **Sign in with Spotify**. Your browser handles the
  sign-in; the app never sees your Spotify password. If Spotify asks you to sign in again after an
  update, that is to approve reading your saved albums.

Each service has **Test connection**. Getting the keys is covered in the
[Installation Guide](INSTALLATION.md#set-up-the-music-services).

## 2. Set up storage (Settings)

Tabletop Simulator plays audio from public URLs, so the MP3s go to Azure Blob Storage. Under
**Settings → Storage**, choose **Sign in to Azure**, then pick the subscription, storage account and
container (or create one). The app checks your access and offers to fix what's missing, such as public
read access on the container. See [Azure Storage setup](AZURE_SETUP.md). A connection string works too,
under **Advanced: use a connection string instead**.

**Folders and audio** sets where downloaded audio and generated files go, the MP3 quality (128 to
320 kbps), and the Tabletop Simulator Saved Objects folder if yours isn't in the default place. The
bundled yt-dlp can be updated from there with **Update yt-dlp**.

## 3. Pick a collection (Collections)

**Collections** lists your playlists, albums and charts from every connected service. Filter by service or
by kind (**Playlists**, **Albums**, **Charts**), or type in **Search or paste a link**: a playlist or album
name, or a Spotify, YouTube or Last.fm link.

Search follows the selected kind; under **All** it finds both playlists and albums, and each result says
which it is. Your own albums are your saved albums on Spotify, YouTube Music album playlists on your
channel, and your top albums on Last.fm.

Album links that work:

- **Spotify:** `https://open.spotify.com/album/…` (from **Share → Copy album link**).
- **YouTube Music:** the link from **Share** on an album, `https://music.youtube.com/playlist?list=OLAK5uy_…`.
  The address-bar link `music.youtube.com/browse/MPREb_…` can't be opened; use Share instead.
  YouTube has no album search of its own, so album search on YouTube finds only the albums its playlist
  search happens to return.
- **Last.fm:** an album page, `https://www.last.fm/music/Artist/Album`. Last.fm has no audio, so each track
  is found by searching, as for Last.fm charts.

## 4. Build it (collection page)

Open a collection to see its tracks. Above them, **Tabletop object** sets the chip image (or **Use custom
image…**), **Show title on chip**, and the object's name, description and image URLs. All of these are
optional.

Choose **Build for Tabletop Simulator**. The app finds and downloads each track's audio, converts it to
MP3, uploads it, and generates the Tabletop Simulator files. Each track shows its status as it goes;
**Cancel build** stops it. Tracks already downloaded or uploaded are reused, so **Build again** is quick.

## 5. Put it in Tabletop Simulator

Choose **Export to TTS**. The music player is added to your Saved Objects. In Tabletop Simulator, open
**Objects → Saved Objects → TTSMixmaster** and click it to place it. See
[TTSMixmaster in Tabletop Simulator](TTS.md) for what the object contains and how it plays.

**Copy Lua script** copies the playlist script instead, and **Open output folder** shows the generated
files.

## Builds

**Builds** lists every finished music player, so you can export one to Tabletop Simulator again, copy its
Lua script, or open its folder without rebuilding.

## Troubleshooting

See the [Troubleshooting Guide](TROUBLESHOOTING.md). Settings and logs live in
`%LOCALAPPDATA%\TTSMixmaster` ([Configuration](CONFIGURATION.md)).
