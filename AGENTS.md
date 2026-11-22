# TTSMixmaster: agent guide

TTSMixmaster turns music-service playlists (Last.fm, YouTube, Spotify) into Tabletop Simulator
music-player objects. It finds each track's audio (yt-dlp + ffmpeg), uploads the MP3s to Azure Blob
Storage, and generates TTS Lua/JSON/save files that point at the public blob URLs.

## Current state: Python â†’ .NET port in progress

- **Legacy (reference only):** the Python app in `src/api`, `src/downloader`, `src/uploader`,
  `src/tts_formatter`, `src/gui`, `src/utils`, `main.py`. It is the behavioral spec for the port.
  Don't add features to it. It is deleted once the .NET app reaches parity.
- **Target:** .NET 10 + WinUI 3 (Windows App SDK 2.x), Windows-only.

```
TTSMixmaster.slnx
src/TTSMixmaster.Core/        net10.0 class library: models, services, downloader, uploader, TTS formatter, config, file layout.
                              No UI or WinUI references, so it is unit-testable and agent-friendly.
src/TTSMixmaster.App/         WinUI 3 app (unpackaged, Windows App SDK self-contained), MVVM via CommunityToolkit.Mvvm.
tests/TTSMixmaster.Core.Tests/            MSTest unit tests for Core.
tests/TTSMixmaster.Core.IntegrationTests/ MSTest integration tests (real tools, Azurite, real files).
tests/TTSMixmaster.App.Tests/             MSTest WinUI UI-thread tests ([UITestMethod]).
tests/e2e/                                winapp ui smoke script (Sandbox/local). See docs/TESTING.md.
installer/msix/               MSIX manifest, assets and PRI config. Releases ship an x64+ARM64 .msixbundle with
                              App Installer auto-update, plus portable zips (scripts/package-release.ps1, docs/RELEASING.md).
```

## Build and test

```powershell
dotnet build TTSMixmaster.slnx -p:Platform=x64
dotnet test --project tests/TTSMixmaster.Core.Tests          # MTP runner (global.json); see docs/TESTING.md for every layer
dotnet publish src/TTSMixmaster.App -c Release -p:Platform=x64   # self-contained folder (win-x64.pubxml)
```

Always pass `-p:Platform=x64` (or ARM64) when building the solution or the App; the solution has no AnyCPU config.

**Run and verify the UI in Windows Sandbox, not on the owner's desktop:**

```powershell
cd src/TTSMixmaster.App
winapp run . --on sandbox --detach --json
winapp ui screenshot --on sandbox -a TTSMixmaster -o <scratch>\shot.png
```

Keep `--on sandbox` on every `winapp ui` command (see `winapp-sandbox`, `winui-ui-testing`). There is one
shared Sandbox: don't stop or close it, and don't assume other workers' apps aren't in it too.
The owner's machine has a Defender ASR rule (01443614-â€¦) that blocks executing or reading freshly built
unsigned exes. The main checkout is excluded, but **Orca child worktrees may not be**. If you hit
"Access denied" on `TTSMixmaster.exe`, report it; don't try to evade the rule. `dotnet TTSMixmaster.dll`
is not a workaround (WinUI self-contained needs the app's own exe manifest).

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
- Tests: **MSTest only** (`MSTest.Sdk` on Microsoft.Testing.Platform, version pinned in `global.json`); never xUnit or NUnit.
  Four layers, each in its own place: unit (`tests/TTSMixmaster.Core.Tests`, fakes only), integration
  (`tests/TTSMixmaster.Core.IntegrationTests`, `[TestCategory("Integration")]`, real tools/Azurite, Inconclusive when
  missing), App UI-thread (`tests/TTSMixmaster.App.Tests`, `[UITestMethod]` for anything touching WinUI objects) and
  e2e (`tests/e2e/ui-smoke.ps1`, AutomationId selectors, Sandbox). Seal test classes, prefer `Assert.ThrowsExactly`,
  `AreSequenceEqual`, `ContainsSingle`, and keep tests that share processes/ports/mutexes `[DoNotParallelize]`.
  Commands per layer: `docs/TESTING.md`.

## Git

- Work on feature branches; `main` is release, `dev` is integration.
- Conventional-commit style subjects (`feat:`, `fix:`, `refactor:`, `chore:`, `ci:`).
