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

**Collection folder**:
The folder in the library that belongs to exactly one collection. Its name pairs the collection's display name with its identity, so it stays the same when the collection is renamed.
_Avoid_: playlist folder

**Collection state**:
The record, kept inside a collection folder, of which tracks are stored there and which have been published. It is the single source of truth for "what do we already have for this collection".
_Avoid_: manifest, index, cache file

**Legacy folder**:
A collection folder from the Python app or an earlier build that is named only after the collection. It is never adopted or changed.

## Tracks

**Track key**:
The stable identity of a track within the library: the service's own track id when it has one, otherwise the track's normalized artist, title, album and duration. It decides the track's file name and is stored inside the file.
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
