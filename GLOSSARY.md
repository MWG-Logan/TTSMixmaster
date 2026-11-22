# TTSMixmaster

Turns music-service playlists into Tabletop Simulator music players: it finds each track's audio,
publishes it to public blob storage, and exports TTS objects that stream it.

## Library

**Library**:
Everything TTSMixmaster keeps on disk for the user's collections: one audio folder and one export folder per collection.
_Avoid_: downloads folder, cache

**Collection**:
A list of tracks from one music service that the user opens as a unit: a playlist, album or chart. A collection is identified by its service and collection id, never by its name.
_Avoid_: playlist (when the kind doesn't matter)

**Album**:
A collection that is one released album, with its tracks in disc and track order and its artist as the owner. Each service gives it its own id: `spotify:album:ID`, a YouTube Music album playlist id, or `lastfm:album?artist=…&album=…`.
_Avoid_: record, release

**YouTube Music album playlist**:
The playlist YouTube generates for an album, whose id starts with `OLAK5uy_`. It is how an album reaches TTSMixmaster from YouTube: YouTube titles it "Album - Name" and owns it as "YouTube", so the app drops the prefix and names the artist from the tracks' "<Artist> - Topic" channel. A `music.youtube.com/browse/MPREb_…` album page is not one, and can't be opened.
_Avoid_: OLAK playlist, auto-generated playlist

**Collection folder**:
The folder in the library that belongs to exactly one collection, as its owner marker records. Its name pairs the collection's display name with its identity, so it stays the same when the collection is renamed.
_Avoid_: playlist folder

**Collection state**:
The record, kept inside a collection folder, of which track file belongs to each track key, what has been published, and the last saved-object export. It is the index we consult first; anything it loses is recovered by re-verifying the files and blobs themselves.
_Avoid_: manifest, cache file

**Owner marker**:
The record in a collection folder naming the one collection it belongs to. A folder whose marker names another collection, or can't be read, is never written to, and a folder with no marker is claimed only when it is new or empty.
_Avoid_: lock file

**Reserved folder**:
The part of a collection folder that holds only the library's own bookkeeping (owner marker, collection state, temporary files), never tracks or exports.

**Legacy folder**:
A collection folder from the Python app or an earlier build that is named only after the collection. It is never adopted or changed.

## Tracks

**Track key**:
The stable identity of a track within the library: the service's own track id when it has one, otherwise the track's normalized artist, title, album and duration. It finds the track's file, names a track file the first time it is stored, and is stored inside that file.
_Avoid_: track id (ambiguous with the service's id), file name

**Track file**:
The local audio file for one track key in a collection folder. It is reused only when it can be verified as that track key.
_Avoid_: download, cached file

**Unverified file**:
A file at a track file's name whose track key can't be confirmed. It is never used, overwritten or deleted.

## Publishing

**Blob**:
A published copy of one recording in Azure Blob Storage, named by the recording's content. Once published, a blob never changes.
_Avoid_: upload (as a noun)

**Export**:
The set of TTS files generated for a collection (Lua scripts, playlist data and a save object) that point at its blobs.
_Avoid_: TTS output, artifacts

**Saved object**:
A collection's music player written into Tabletop Simulator's Saved Objects so it can be spawned from inside TTS: a save file and its same-named thumbnail, owned and checked as one pair.
_Avoid_: TTS export (ambiguous with Export), copied Lua
