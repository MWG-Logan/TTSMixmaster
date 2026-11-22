# Library layout: owned collection folders, per-collection state, content-addressed blobs

Status: accepted (2026-11-21; revised after the 69ed452 review)

Before this decision, folders, track files, blobs and exports were named from display text. As a result,
same-named playlists shared folders and exports, reordering a playlist could swap cached audio, and two
collections' `Artist - Song.mp3` overwrote one blob. We now key every name on a stable identity. Each
collection folder is claimed with an owner marker. Track files are resolved through a small per-collection
state file. Blobs are addressed by content. `LibraryLayout` and `CollectionState` own all of it: the
downloader, uploader, formatter and TTS exporter ask them and build no library paths.

**Reserved folder.** Each collection folder has a hidden `.ttsmixmaster\` subfolder for the library's
own bookkeeping:
- `owner.json`: the owner marker, holding service, collection id and name.
- `collection.json`: the state file.
- `tmp\`: every temp file.

No display-derived name can reach it: track files end in ` [hash].mp3` and exports have extensions. So
an export can never replace the state file, even when the audio and export roots are the same folder.

**Collection folder.** `<root>\<Name> [<service>-<id hash>]`, e.g. `My Playlist [spotify-3fa9c1d2e4]`,
under both the download root and the export root. The hash is the first 10 hex digits of SHA-256 of
`service:id`, which is case-safe, short and readable. A folder with this suffix *and* a matching owner
marker is reused after a rename. Every write path (download, export) first calls the one operation
`ClaimFolder`, which writes the marker on first use. If the marker names another collection (a real
40-bit collision with an equal name) or is unreadable, it fails closed and touches nothing.

**Track key.** `<service>:<service track id>`, or `<service>:meta:<hash of NFKC-normalized
artist|title|album|duration>`. The state file maps each key to its file, and lookups go through that
mapping first. So a retitled track, or the same title in NFC versus NFD, keeps its file. Only a key never
stored here gets a new name, `<Artist - Title> [<8 hex of key hash>].mp3`. The key is also embedded in the
MP3 (`TXXX:TTSMIXMASTER_TRACK_KEY`).

**State file.** `collection.json` maps track key → file, length, SHA-256, blob name/URL and upload time,
plus the last TTS export (path and hash).
- **Parsing is strict.** Required fields are enforced, nulls are rejected, and unknown members are
  disallowed. An incomplete, foreign (e.g. a TTS save) or null-bearing document is corrupt, never an
  empty state. A corrupt file is renamed to `collection.json.corrupt-<time>` (kept) and the state starts
  empty.
- **Updates are serialized.** Each read-modify-write runs under one named mutex per folder
  (`Local\TTSMixmaster-<path hash>`), across threads and app instances. The file is replaced atomically.
- **Reuse is verified.** A cached file is reused if its entry matches (key and length) or, with no entry,
  if its embedded key matches. A file at our name that can't be verified fails that track and is never
  overwritten or deleted.
- **It is the index, not the only truth.** Lost track entries are recovered by re-verifying embedded keys.
  Lost upload entries are recovered by re-hashing and re-checking the blob.

**Blob name.** `audio/<64-hex SHA-256 of the bytes>.mp3`, content only. The same bytes always share one
blob and different bytes never do. The friendly name travels in `Content-Disposition` (ASCII `filename`
plus RFC 5987 `filename*`), and metadata values are percent-encoded UTF-8.
- An existing blob is reused only when its length *and* its `content_sha256` metadata match the full hash.
- A blob without that metadata, or with a different value, fails the track and is left unchanged. It is
  not downloaded to check, and not overwritten.
- Uploads use `If-None-Match: *`. A 409/412 response is re-verified the same way.
- `azure_blob_freshness_days` is ignored.

**Writes.**
- Every write is staged in the destination folder's `.ttsmixmaster\tmp\` and then renamed (same volume).
- Downloads never replace a non-empty file. The state is updated only after the file it describes is final.
- Multi-file writes stage every file before renaming any. Each file ends up whole, old or new, but a set
  of files is not transactional, so the file that commits a set is renamed last.
- The sweep deletes only files in `tmp\` that match a generated GUID name, are older than a day, and
  belong to a folder this collection has just claimed.
- A failed state write after a download fails that track, and its file is adopted next run. A failure in
  the initial claim fails the batch. A failed upload-record write is ignored, because the blob is valid
  and can be re-derived.

**Export to TTS Saved Objects.** `TtsExporter.ExportToTtsAsync` writes `<Name>.json` plus a 256×256
`<Name>.png` into `<Saved Objects>\TTSMixmaster\`.
- **Location.** Saved Objects is `<Documents>\My Games\Tabletop Simulator\Saves\Saved Objects`.
  Documents comes from the `MyDocuments` known folder, which follows OneDrive redirection. The
  `tts_saved_objects_path` setting overrides it. If `...\Saves` is missing, TTS is reported as not found
  and nothing is created.
- **File format.** The file is a save-format document with empty `SaveName`, `Date` and `VersionNumber`
  and one `ObjectStates` entry, centred on the table.
- **Naming.** The name is the friendly name, or `<Name> [<service>-<hash>]` when that is taken by a file
  we have no record of writing.
- **Re-export and conflicts.** Re-export replaces the file recorded in the state file. If that file's hash
  differs from what we wrote, the result is `Conflict` (the UI offers Replace / Keep both / Cancel) and
  nothing is written. The `.png` is renamed before the `.json`, so the `.json` is the commit point. The
  reserved folder is removed afterwards so TTS's browser doesn't list it.
- **Thumbnail.** The thumbnail is the collection's own image when it downloads as PNG or JPEG, otherwise
  a generated placeholder. Unity loads either format by content; this is not yet confirmed in-game.
- **Sources.** The official TTS Knowledge Base documents the save format (SaveState with ObjectStates)
  but not Saved Objects (https://kb.tabletopsimulator.com/custom-content/save-file-format/). The location,
  shape, subfolders and 256×256 same-name `.png` were therefore confirmed against an existing TTS install
  on the owner's machine (OneDrive-redirected Documents). The tts-backup tool's Windows data root
  `~/Documents/My Games/Tabletop Simulator` agrees
  (https://raw.githubusercontent.com/eigengrau/tts-backup/master/src/tts_tools/libtts.py).

**Legacy folders: ignored.** Name-only folders from the Python app or earlier builds are never read,
adopted, moved or deleted. Their identity can't be verified, and they may mix same-named playlists.
Tracks download again once. `LibraryLayout.LegacyAudioFolder` lets the App offer a manual cleanup.
Legacy `audio/<file name>` blobs are outside the new name space, so old exports keep working.

## Considered options

- **Numbered collision suffixes**: order-dependent names. A lost map brings the swap back.
- **One-time import of legacy files**: trusts content we can't verify, and doubles disk use.
- **Blob names with a display segment, or keyed by track**: break deduplication by bytes, or overwrite
  when a track is re-downloaded with different bytes.
- **Downloading a blob that has no hash metadata in order to hash it**: costs a full download and needs
  extra concurrency handling. Failing closed is simpler, and such blobs aren't ours.
- **A lock file, or a single app instance, instead of a named mutex**: a lock file needs stale-lock
  recovery, and a single instance is an App policy. The mutex is released by the OS when its holder dies.
