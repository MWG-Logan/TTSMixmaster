# Python reference artifacts

Run from the worktree root:

```powershell
$env:PYTHONPATH = (Get-Location).Path
& C:\Users\Logan\source\repos\TTSMixmaster-golden-venv\Scripts\python.exe -I tests/TTSMixmaster.Core.Tests/Tts/golden/generate_goldens.py
```

The coordinator supplied this CPython 3.11.15 venv because the main checkout's
`.venv` points at a deleted Python 3.11.9 executable. It already contains requests
and azure-storage-blob; this worker installed nothing into it. `-I` ignores
PYTHONPATH, so the generator explicitly inserts its worktree root in `sys.path`.
It imports the actual legacy formatter and executes its formatting methods, never
the returned Lua. The Python source remains unchanged.

Three fixed collections exercise ordinary tracks, no tracks, missing URLs, double
quotes, backslashes, LF/CR/tab, accented and Japanese text, and an emoji. Each has
five raw Python outputs: player Lua, simple playlist Lua, data JSON, simple save,
and full-player save. The generated UTF-8 files use LF and no BOM; this is an
explicit Python output setting, not a normalization in C# comparisons.

## Nondeterminism

The only normalization is to supply `object_guid="ABC123"` to Python and C# when
comparing saves. Python generates no timestamps in these artifacts. Production
saves get a fresh six-character uppercase hexadecimal GUID. No whitespace, URLs,
JSON property order, Unicode or numeric tokens are normalized during comparison.

## Intentional differences (goldens retain Python's original output)

The tests explicitly transform only these known defects before byte comparison:

1. `generate_simple_playlist_lua` in legacy `src/tts_formatter/tts_formatter.py`,
   function starting at line 347, appends a 400-space-padded `--[[Object base code]]`
   payload on line 375. It
   schedules a `getObjects()` scan with `Wait.time`, appends its own script to
   each existing object's Lua, hooks `onObjectSpawn` to do the same to newly
   spawned objects, and modifies selection behavior through `onPlayerAction`.
   It also calls `WebRequest.get("https://obje.glitch.me/")`: a nonempty response
   beginning with `true` supplies Lua after that prefix, which it splices into
   its own script with `setLuaScript` before `self.reload()`. This is remote
   code loading and self-propagation, not playlist functionality. The C# simple
   playlist keeps only the static playlist table and its two trailing linefeeds.
   No remote endpoint was contacted and none of this Lua was executed.
2. The full player declares `local MusicPlayer`, shadowing TTS's global engine.
   Rename its local controller to `TtsMusicPlayer`, preserving engine calls to
   the global `MusicPlayer` and updating usage messages.
3. Lua reserves `repeat`; replace controller `.repeat` with `["repeat"]`.
4. Escape the collection name in full-player Lua strings and its leading comment,
   just as track strings are escaped. Otherwise quotes or newlines break Lua or
   allow unintended source text. This also handles literal template markers.
5. Correct full-player engine calls: `setPlaylist` takes audioclip tables with
   `url` and `title`, `play` needs a current clip (set through `setCurrentAudioclip`),
   and the documented engine exposes `pause`, not `stop`. The controller's stop
   command pauses playback. See https://api.tabletopsimulator.com/musicplayer/.

Additional deliberate behavior differences outside the fixed golden inputs:

- Uploads retain their `DownloadedTrack.Track`; the formatter aligns by track
  reference (then record equality for reconstructed tracks), never filename stem
  or upload ordering. Equal-valued distinct track objects remain distinguishable.
- Public blob URLs use the SDK endpoint and escape literal filename delimiters
  such as `#` and `%`, unlike Python's raw concatenation. SAS query strings are
  excluded. The stable blob name itself remains `audio/<on-disk filename>`.
- Local file fallback uses a valid file URI (`file:///C:/...` on Windows) instead
  of the legacy PlaylistInfo path's malformed `file://C%3A...` URI.
- Writes follow `LibraryLayout.TtsFolder` with safe names, UTF-8 without BOM and
  atomic temp-file replacement. The task requests all four artifact types, while
  the current Python GUI's save method writes only its save JSON.
- Connection failures return bounded readable messages rather than raw SDK
  exception text, which can expose connection details. Settings supply Azure
  credentials; the Core contract does not expose Python's account-key/force
  parameters or its implicit environment-variable fallback.
- MIME types for supported audio formats are fixed to the legacy Windows host's
  Python `mimetypes.guess_type` results (including its registry mappings), with
  `audio/mpeg` as the unknown-extension fallback; another machine's MIME registry
  cannot silently change an upload's type.

No live Azure operations or TTS runtime playback were used for these tests.

## Worker validation and integration

On branch `feat/port-publish`, based on scaffold `cf98f58`:

- `dotnet build src/TTSMixmaster.Core`: exit 0, zero warnings/errors.
- `dotnet build tests/TTSMixmaster.Core.Tests`: exit 0, zero warnings/errors.
- `dotnet test tests/TTSMixmaster.Core.Tests`: exit 0, 45 passed, zero failed/skipped
  (10 baseline tests, 24 uploader cases, 11 formatter cases).
- Golden tests compare all 15 reference artifacts; another three cases check the
  four production-written files for each collection, normalizing only the GUID.
- Only assigned paths changed; contracts, configuration, models, LibraryLayout,
  and UI files are untouched.
- `git diff --check` passes for implementation/tests; the unchanged Python golden
  files deliberately retain one whitespace-only playlist line and the simple
  playlist's trailing blank line.

The original full-solution baseline build failed on App output-directory access
under this worktree. The coordinator explicitly replaced the worker gate with the
Core commands above and owns the full-solution build after merging. An initial
NU1900 feed warning cleared on the final runs; there were no vulnerability warnings.

Construct the uploader with `new AzureBlobUploader(() => settings)` and the
formatter with `new TtsFormatter(libraryLayout)`. The uploader's optional
TimeProvider and BlobContainerClient factory are the offline test seams; no extra
network abstraction or mocking package is needed. `GenerateAsync` returns the
simple Lua embedded in the default save and paths for all four written artifacts;
`GenerateSaveFile(..., useSimpleFormat: false)` also exposes the full-player save.
The only new package is pinned `Azure.Storage.Blobs` 12.30.2, the latest stable
verified at https://www.nuget.org/packages/Azure.Storage.Blobs/12.30.2.
