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

Each attempt writes a unique `.tts-download-<guid>.*` prefix beside the destination.
The MP3 is tagged through TagLibSharp before its final rename. All owned partials
are removed on failure/cancellation. A crash can leave a hidden partial but cannot
publish it as a completed track. Non-empty final files are skipped. Colliding
names receive numeric suffixes; exact duplicates share a path and get a separate
result/progress entry (normally `Skipped` after the first download). Use returned
`DownloadedTrack.FilePath` downstream rather than reconstructing collision names.

yt-dlp extracts MP3 at the configured kbps. Because it preserves existing MP3
sources without changing bitrate, the downloader uses ffmpeg to re-encode when
the resulting bitrate differs. TagLibSharp handles all metadata; ffmpeg is not
used for metadata remuxing. The fetch script includes ffprobe alongside ffmpeg
for yt-dlp, verifies pinned SHA-256 hashes before publication, and retains the
FFmpeg GPL license. It does not wire installation or CI.

Legacy behavior intentionally omitted:

- Mono conversion: no callers in `src/gui` or `src/tts_formatter`; preserve channels.
- `playlist_manifest.txt`: neither GUI nor formatter reads it. The legacy
  `get_playlist_completeness` helper is unused there; batch results provide status.
- Python package/bundling workarounds and unconverted audio when ffmpeg is absent:
  this implementation requires executable tools and publishes only MP3s.
- Album-based filename suffixes and collapsing exact duplicates out of the result
  list: numeric suffixes are simpler, and every input track retains a result.
- Album terms in search queries: use the requested `Artist - Title` query.

External provider availability still determines whether a source/search succeeds.
Search fallback is a best-effort match, not verification of the recording's identity.
