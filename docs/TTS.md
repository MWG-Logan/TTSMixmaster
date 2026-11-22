# TTSMixmaster in Tabletop Simulator

How the music players TTSMixmaster makes behave inside Tabletop Simulator (TTS), and what the files it
writes contain. For using the app itself, see the [Usage Guide](USAGE.md).

## Putting a music player on the table

1. In TTSMixmaster, open a collection and choose **Build for Tabletop Simulator**. When the build finishes,
   choose **Export to TTS** (on the collection, or later from **Builds**).
2. In Tabletop Simulator, open **Objects → Saved Objects → TTSMixmaster** and click the music player.
   It is placed at your cursor.

Export writes into TTS's own Saved Objects folder
(`Documents\My Games\Tabletop Simulator\Saves\Saved Objects\TTSMixmaster\`, or the folder set under
**Settings → Folders and audio**). Each player is a `<Name>.json` saved object plus a same-named 256×256
`.png` thumbnail that TTS shows in its browser. Exporting the same collection again replaces its own
object in place. If the files were changed outside TTSMixmaster since the last export, the app asks
whether to replace them, keep both, or cancel.

## What the object is

The saved object is a music **chip**: a TTS Custom Tile whose front image is the collection's chip art
(album or playlist art in the chip frame, with the collection name) and whose back is the original
chip back. You can change the name, description and front/back image URLs on the collection page before
building; empty fields use the defaults.

The chip's Lua script is only a static track list:

```lua
playlist = {
    {
        title = "Artist - Title (Album)",
        url = "https://<account>.blob.core.windows.net/<container>/audio/<file>.mp3",
    },
    ...
}
```

The chip doesn't play anything by itself. It is meant for a music-box object that reads the `playlist`
table from chips placed on it, such as the
[Music Box](https://steamcommunity.com/sharedfiles/filedetails/?id=3114355269) on the Steam Workshop.
The script has no other code: it doesn't touch other objects, spawn anything, or load code from the
internet.

## Audio URLs

TTS downloads audio by URL on each player's machine, so every track URL must be publicly readable over
HTTPS. TTSMixmaster uploads the MP3s to your Azure Blob Storage container and writes their public blob
URLs into the playlist (see [Azure Storage setup](AZURE_SETUP.md); the container needs public blob read
access).

If a track couldn't be uploaded, its URL falls back to the local `file:///` path of the downloaded MP3.
That works only on the machine that built it; other players hear nothing for that track. Tracks with
no audio at all have an empty URL.

## Other files a build writes

Each build also writes these files to a per-collection subfolder of the **Generated Lua and save files**
folder (Settings → Folders and audio), for copying by hand or for other tools:

| File | Contents |
|---|---|
| `<name>_playlist.lua` | The same `playlist = { ... }` table the chip carries. **Copy Lua script** on the collection page copies this. |
| `<name>.lua` | A standalone controller script (`TtsMusicPlayer`) that drives TTS's built-in Music Player (`MusicPlayer.setCurrentAudioclip`, `play`, `pause`) with play, stop, next, previous, shuffle and repeat. Paste it into any object's script. |
| `<name>_data.json` | The track list as JSON (`name`, `url`, volume and loop settings) for other tools. |
| `<name>.json` | A TTS save-format document containing the chip. |

The standalone controller follows the documented
[MusicPlayer API](https://api.tabletopsimulator.com/musicplayer/): it sets the current audio clip before
playing, and its stop command pauses, because the engine has no stop call.
