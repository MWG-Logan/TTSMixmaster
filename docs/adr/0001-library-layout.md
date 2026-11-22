# Library layout: identity-keyed folders, per-collection state, content-addressed blobs

Status: accepted (2026-11-21)

Before this decision, collection folders, track files and blob names were derived from display names,
so same-named playlists shared folders and exports, reordering a playlist could swap cached audio, and
two collections' `Artist - Song.mp3` overwrote one blob. We now key every name on a stable identity,
keep one small state file per collection, and address blobs by content. `LibraryLayout` and
`CollectionState` own all of it; the downloader, uploader and formatter ask them and build no paths.

**Collection folder.** `<root>\<Name> [<service>-<id hash>]`, e.g. `My Playlist [spotify-3fa9c1d2e4]`,
under both the download root and the TTS root. The hash is the first 10 hex digits of SHA-256 of
`service:id`, so it is case-safe (YouTube/Spotify ids are case-sensitive, NTFS is not), short and free of
invalid characters, while the name keeps it readable. An existing folder with the same suffix is reused
even if the playlist was renamed. If its state file names a different collection (a hash collision),
we fail loudly rather than share.

**Track key.** `<service>:<service track id>` when the service gives one, else
`<service>:meta:<hash of normalized artist|title|album|duration>`. The local file name is
`<Artist - Title> [<key hash>].mp3`: a pure function of the key, so playlist order never matters. The
key is also written into the MP3 (ID3 `TXXX:TTSMIXMASTER_TRACK_KEY`).

**State file.** `collection.json` in the audio folder: schema version, collection summary, and per
track key its file name, length, SHA-256, blob name, URL and upload time. It is replaced atomically
under an in-process lock, and updated only after the file it describes is final. A cached file is
reused only when its entry matches (same key, file name and length), or, with no entry, when the file's
embedded key matches. Recovery is lazy and conservative: a missing state file starts empty, and a corrupt
or unknown-schema one is renamed to `collection.json.corrupt-<time>` and starts empty. Each file is then
re-verified by its embedded key on next use. A file at our name that cannot be verified fails that track
with a message; it is never overwritten or deleted.

**Blob name.** `audio/<first 32 hex of SHA-256 of the bytes>/<ASCII name>.mp3`. Same bytes always map to
the same blob, and different bytes never share one, so an upload never changes audio that an existing
export points to. An existing blob is reused only if its length (and recorded hash, when present)
matches. Otherwise the track fails. Uploads use `If-None-Match: *`. The freshness window
(`azure_blob_freshness_days`) no longer applies. Metadata values are percent-encoded UTF-8 and
Content-Disposition carries an ASCII `filename` plus an RFC 5987 `filename*`, because Azure headers must
be ASCII.

**Legacy folders: ignore.** Name-only folders from the Python app or earlier runs are never read,
adopted, moved or deleted. Their identity can't be verified (that is the defect being fixed), and they
may already mix tracks from same-named playlists. Tracks are downloaded again into the new folder, once.
`LibraryLayout.LegacyAudioFolder` reports such a folder so the App can offer the user a manual cleanup
(this matters with a near-full disk). Legacy blobs (`audio/<file name>`) are outside the new name space,
so old exports keep working.

**Writes.** Every write is a temp file named `.tts-tmp-<guid>…` in the destination folder, then a rename.
Downloads never overwrite a non-empty file; exports and the state file replace atomically. The formatter
stages all four exports before renaming any. Temps are deleted on failure or cancellation, and leftover
`.tts-tmp-*` files older than a day are swept when a collection is next opened. A failed state write
(disk full, locked file) fails only that track; its file is adopted by embedded key on the next run.

## Considered options

- **Numbered collision suffixes plus a persisted map** keep prettier names, but a lost map brings the
  swap back. Hashed names are self-describing.
- **One-time import of legacy files by tag** saves re-downloads, but it trusts unverifiable content and
  doubles disk use anyway (copy), or modifies user folders (move).
- **Blob keyed by track key** would overwrite when the same track is re-downloaded with different bytes.
  Content addressing makes that impossible.
