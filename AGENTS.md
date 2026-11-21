# TTSMixmaster: agent guide

TTSMixmaster turns music-service playlists (Last.fm, YouTube, Spotify) into Tabletop Simulator
music-player objects. It finds each track's audio (yt-dlp + ffmpeg), uploads the MP3s to Azure Blob
Storage, and generates TTS Lua/JSON/save files that point at the public blob URLs.

## Current state: Python → .NET port in progress

- **Legacy (reference only):** the Python app in `src/api`, `src/downloader`, `src/uploader`,
  `src/tts_formatter`, `src/gui`, `src/utils`, `main.py`. It is the behavioral spec for the port.
  Don't add features to it. It is deleted once the .NET app reaches parity.
- **Target:** .NET 10 + WinUI 3 (Windows App SDK 2.x), Windows-only.

```
TTSMixmaster.slnx
src/TTSMixmaster.Core/        net10.0 class library: models, services, downloader, uploader, TTS formatter, config, file layout.
                              No UI or WinUI references, so it is unit-testable and agent-friendly.
src/TTSMixmaster.App/         WinUI 3 app (unpackaged, Windows App SDK self-contained), MVVM via CommunityToolkit.Mvvm.
tests/TTSMixmaster.Core.Tests/ xUnit tests for Core.
installer/                    WiX MSI. Harvests the App publish folder.
```

## Build and test

```powershell
dotnet build TTSMixmaster.slnx -p:Platform=x64
dotnet test tests/TTSMixmaster.Core.Tests
dotnet publish src/TTSMixmaster.App -c Release -p:Platform=x64   # self-contained folder (win-x64.pubxml)
```

Always pass `-p:Platform=x64` (or ARM64) when building the solution or the App; the solution has no AnyCPU config.

**Running the app locally is currently blocked on the owner's machine.** A Defender ASR rule
("block executables unless prevalence/age/trusted", 01443614-…) denies both executing and *reading*
freshly built unsigned `TTSMixmaster.exe`, so `winapp run`, `winapp run --on sandbox`, and
`winapp ui` against the app all fail. `dotnet TTSMixmaster.dll` does not work either: WinUI self-contained
needs the app's own exe manifest. Do not try to evade the rule. Verify UI with build + WinUI analyzer +
`winui-code-review`. If you get a launch to work because an exclusion was added, use
`winui-ui-testing` / `winapp-ui-automation` for screenshots.

## Agent tooling (vendored, same for Claude and Codex)

- `.claude/skills/` and `.agents/skills/` carry Microsoft's WinUI + WinApp CLI skills (mirrored copies).
  Refresh with `scripts/sync-agent-skills.ps1`; don't hand-edit them.
  - `winui-design`: **read before writing any XAML.** Use `winapp find-ui` / `winapp find-api` instead of guessing controls/APIs.
  - `winui-dev-workflow`: build/run/fix errors. `winui-code-review`: review gate for UI changes.
  - `winui-ui-testing`, `winapp-ui-automation`, `winui-devtools`: live UI verification (see the ASR caveat above).
  - `winui-packaging`, `winapp-*`: packaging, signing, SDK setup.
- `winapp` CLI (WinApp CLI 0.8+) is installed machine-wide.
- `impeccable` (user-level skill) is **web/iOS/Android-oriented**. Its detector, live mode and screenshot
  tooling don't apply to WinUI, and its craft floor (custom fonts, own visual world) fights Fluent.
  Use it only for platform-neutral UX thinking (`shape` before building a flow, `critique` heuristics,
  `clarify` for copy, `harden`/`onboard` for empty, error and first-run states). Native Fluent
  conventions from `winui-design` win any conflict.

## Conventions

- C#: file-scoped namespaces, nullable enabled, `_camelCase` fields, `Async` suffix, `Is/Has/Can` bool prefixes.
- MVVM with `[ObservableProperty]` / `[RelayCommand]`; `x:Bind` (not `Binding`); `AutomationProperties.AutomationId` on every interactive control.
- Core services take dependencies through constructors (interfaces where there's a real seam: HTTP, process launch, filesystem roots, clock). No static singletons.
- Long-running work is `async` with `CancellationToken` and `IProgress<T>`; no `Task.Result`/`.Wait()`.
- External tools (`yt-dlp.exe`, `ffmpeg.exe`) run as child processes, located through one resolver, and ship next to the app. yt-dlp can self-update (`-U`) without an app release.
- **KISS + reliability over cleverness.** One obvious place for each concern (especially file paths and layout).
- `config.json` stays readable by the .NET app (same snake_case keys) so existing users keep their settings.
- Secrets never get logged or committed.

## Git

- Work on feature branches; `main` is release, `dev` is integration.
- Conventional-commit style subjects (`feat:`, `fix:`, `refactor:`, `chore:`, `ci:`).
