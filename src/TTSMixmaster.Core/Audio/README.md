# Audio downloader

Construct `AudioDownloader(ToolLocator, IProcessRunner, LibraryLayout, Func<AppSettings>)`;
use `ProcessRunner` in the app and a fake `IProcessRunner` in tests. No UI dependencies.
`CheckToolsAsync` returns null on success. It invokes yt-dlp `--version` and ffmpeg
`-version` (ffmpeg uses a single dash). Tool resolution belongs only to `ToolLocator`:
updated copies in `AppPaths.ToolsDirectory`, bundled copies, then PATH.

Downloads are sequential. A recognized HTTP(S) media source is tried first, then
`ytsearch1:Artist - Title`, then `scsearch1:Artist - Title`, matching the legacy
engine order. Individual failures return `Failed`; cancellation throws
`OperationCanceledException` and stops the batch. Progress indices are 1-based.
Processes have a 15-minute timeout (15 seconds for version checks); cancellation
and timeouts kill the process tree. User yt-dlp configuration is ignored so it
cannot override the destination or post-processing arguments.

File names, reuse and the state file belong to `LibraryLayout` (docs/adr/0001-library-layout.md).
Each batch first claims the collection's identity-keyed folder (`<Name> [<service>-<id hash>]`). The
claim checks the owner marker in the hidden `.ttsmixmaster\` subfolder and fails the batch closed if the
folder belongs to another collection. Each track is resolved by its track key through
`.ttsmixmaster\collection.json` first. A key never stored here gets `Artist - Title [<key hash>].mp3`,
and keeps that file even if the service later changes the title. A cached file is reused (`Skipped`) when
its entry matches (same length), or, with no entry, when the track key embedded in its ID3 tag
(`TXXX:TTSMIXMASTER_TRACK_KEY`) matches. A non-empty file that can't be verified fails the track and is
left alone. Exact duplicates share a key and a file and get a separate result/progress entry (normally
`Skipped` after the first download).

Each attempt writes `<guid>.*` partials in the folder's `.ttsmixmaster\tmp\`, never beside final files.
The MP3 is tagged (including the track key) through TagLibSharp before its final rename, which never
replaces a non-empty file. The state file is updated only after that rename, under a per-folder named mutex
shared with other app instances. If the update fails (for example disk full or a locked file), the
track reports `Failed` and its file is adopted by embedded key on the next run. All owned partials are
removed on failure or cancellation. Crash leftovers in `tmp\` are swept after a day. Use the returned
`DownloadedTrack.FilePath` downstream rather than rebuilding names.
yt-dlp extracts MP3 at the configured kbps. Because it preserves existing MP3
sources without changing bitrate, the downloader uses ffmpeg to re-encode when
the resulting bitrate differs. TagLibSharp handles all metadata; ffmpeg is not
used for metadata remuxing. The fetch script includes ffprobe alongside ffmpeg
for yt-dlp, verifies pinned SHA-256 hashes before publication, and retains the
FFmpeg GPL license. It does not wire installation or CI.

Legacy behavior intentionally omitted:

- Mono conversion: the legacy GUI and formatter never called it; preserve channels.
- `playlist_manifest.txt`: neither GUI nor formatter reads it; `collection.json` replaces it.
- Legacy name-only folders are ignored (never read or changed); see `LibraryLayout.LegacyAudioFolder`.
- Legacy Python bundling workarounds and unconverted audio when ffmpeg is absent:
  this implementation requires executable tools and publishes only MP3s.
- Album-based filename suffixes and collapsing exact duplicates out of the result
  list: names carry a track-key hash instead, and every input track retains a result.
- Album terms in search queries: use the requested `Artist - Title` query.

External provider availability still determines whether a source/search succeeds.
Search fallback is a best-effort match, not verification of the recording's identity.
