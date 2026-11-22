# Testing

All .NET tests use **MSTest** (`MSTest.Sdk`) on **Microsoft.Testing.Platform** (MTP). Don't add xUnit or NUnit.
`global.json` pins the SDK band, the `MSTest.Sdk` version and selects MTP for `dotnet test`. Bump
`MSTest.Sdk` by hand in `global.json`: Dependabot and Visual Studio don't update MSBuild SDK versions.

The layers follow Microsoft's guidance for WinUI 3 desktop apps: fast unit tests for the UI-free Core,
integration tests against real tools and emulators, UI-thread tests in a self-hosted WinUI test app,
and a few end-to-end UI Automation checks against the running app.

| Layer | Project / script | What it covers | Needs | Where it runs |
|---|---|---|---|---|
| Layer | Project / script | Tests | What it covers | Needs | Where it runs |
|---|---|---|---|---|---|
| Unit | `tests/TTSMixmaster.Core.Tests` | 327 | Core logic with fakes (HTTP, processes, clock, folders) | Windows | Every PR and push |
| Integration | `tests/TTSMixmaster.Core.IntegrationTests` (`[TestCategory("Integration")]`) | 10 | Real filesystem, real yt-dlp/ffmpeg against a loopback media server, uploader against Azurite, formatter/exporter writing into an injected TTS root | `tests/.tools` (fetch-tools), Azurite; Inconclusive without them | Windows runner, after fetch-tools |
| App (UI thread) | `tests/TTSMixmaster.App.Tests` | 84 | View models against the real composition root with Core faked, converters, the export result bar and its dialog, on the WinUI dispatcher (`[UITestMethod]`) | Windows App SDK (self-contained), an interactive session | Windows runner |
| End to end | `tests/e2e/ui-smoke.ps1` | 9 checks (15 with `-DevSource`) | The running app through UI Automation (`winapp ui`), AutomationId selectors: every page, Settings, and with `-DevSource` a Build + Export to TTS | winapp CLI, Windows Sandbox (or local desktop); `-DevSource`: node + Azurite on the host | Locally / Sandbox, against a build or the published folder |

Counts are test cases (data rows count once each) as of the e2e/App-test work in November 2026.

Nothing touches real cloud services or credentials. Integration tests block yt-dlp's online search fallback,
and use Azurite's public development key.

## Commands

Run from the repo root. The test projects build themselves; add `--no-build` after a solution build.

```powershell
# Unit (every PR/push)
dotnet test --project tests/TTSMixmaster.Core.Tests -c Release -- --report-trx --results-directory TestResults/unit

# Integration (Windows runner)
powershell -File scripts/fetch-tools.ps1 -OutputFolder tests/.tools   # or set TTSMIXMASTER_TEST_TOOLS
npm install -g azurite                                                # CI only; or set AZURITE_BLOB_ENDPOINT
dotnet test --project tests/TTSMixmaster.Core.IntegrationTests -c Release -- --filter "TestCategory=Integration" --report-trx --results-directory TestResults/integration

# App UI-thread tests (Windows runner; Platform must match the runner: x64 or ARM64)
dotnet test --project tests/TTSMixmaster.App.Tests -c Release -p:Platform=x64 -- --report-trx --results-directory TestResults/app

# Everything in the solution at once (unit + integration + App)
dotnet test --solution TTSMixmaster.slnx -c Release -p:Platform=x64

# End to end, in Windows Sandbox (see AGENTS.md; the script never stops the Sandbox)
tests/e2e/ui-smoke.ps1 -Target sandbox                                  # winapp run of the App project (Debug build)
tests/e2e/ui-smoke.ps1 -Target sandbox -PublishDir <publish folder>     # a published build

# End to end with Build + Export to TTS: a dev-source publish (a local collection stands in for YouTube)
dotnet publish src/TTSMixmaster.App -c Release -p:Platform=x64 -p:TtsmDevSource=true -o <folder>
Copy-Item tests/.tools/yt-dlp.exe, tests/.tools/ffmpeg.exe, tests/.tools/ffprobe.exe <folder>   # after fetch-tools
npm install -g azurite                                                  # once; node.exe and Azurite are copied into the guest
tests/e2e/ui-smoke.ps1 -Target sandbox -PublishDir <folder> -DevSource

# Local desktop (prefer the Sandbox)
tests/e2e/ui-smoke.ps1 -Target local -AppExe <publish folder>\TTSMixmaster.exe
```

The script runs under Windows PowerShell 5.1 or PowerShell 7 (`powershell -ExecutionPolicy Bypass -File …`
if scripts are blocked).

MTP options go after `--`. Filter examples: `--filter "TestCategory=Integration"`,
`--filter "FullyQualifiedName~TtsExport"`. The exit code is non-zero when a test fails. Inconclusive tests
(missing tools or Azurite) count as skipped and don't fail the run, so CI that must exercise them should
check the TRX for skipped tests or make sure the prerequisites are installed.

## Notes per layer

**Unit.** Test classes run in parallel (class level, as under xUnit). Classes that hold machine-wide resources
are `[DoNotParallelize]`: `ProcessRunnerTests` (timing-sensitive child processes), `SpotifyAuthenticationTests`
(loopback ports), `ReviewRound2Tests` (child processes and the named state mutex) and `AzureAccountTests`
(the real `%LocalAppData%\.IdentityService` folder). New tests that use those resources belong in those
classes or need the same attribute. `Program.cs` is the custom entry point: it starts the MTP host, or the
`state-writer` child process that the cross-process test launches.

**No crash dialogs, any layer.** Tests run while people use the machine. Every test host calls
`NoCrashDialogs.Apply()` (`tests/Shared`, linked into each project) before anything else: Core.Tests' and the
integration tests' `Program.Main`, and the App.Tests host's constructor. It turns off WER, critical-error and
open-file dialogs for the host and every process it starts (the state-writer child, yt-dlp, ffmpeg, Azurite).
Host setup failures (for example MTP's named pipe denied in a sandbox) and helper failures are caught, written
to stderr and end the run with a non-zero exit code. A new helper exe needs the same two things.

**Transient file locks.** Replacing a just-written file now and then fails with "access denied" while on-access
antivirus scans it (about 1 in 100 back-to-back replaces on a dev machine with Defender). Core replaces files
through `FileReplace.Move`, which retries only those errors for about a second. New code that replaces a file
with `File.Move(…, overwrite: true)` should use it too; tests for it are in `Files/FileReplaceTests.cs`.

**Integration.** Runs one test at a time (`[assembly: DoNotParallelize]`).
- Tools come from `tests/.tools` (gitignored) or `TTSMIXMASTER_TEST_TOOLS`. Tools on PATH are ignored.
- Azurite: `AZURITE_BLOB_ENDPOINT` (for example `http://127.0.0.1:10000/devstoreaccount1` from a service
  container), else `azurite-blob` on PATH, started per class on a free port with in-memory storage and stopped
  afterwards. Nothing is installed by the tests.
- Each missing prerequisite ends the affected tests Inconclusive with the command that fixes it.

**App.** `tests/TTSMixmaster.App.Tests` was started from `winapp new -t winui-unittest` and moved to MSTest.Sdk:
`UnitTestApp` is a self-hosted, unpackaged WinUI app that opens a window, sets
`UITestMethodAttribute.DispatcherQueue` and runs MTP. Use `[UITestMethod]` for anything that touches
WinUI objects and `[TestMethod]` otherwise. `UnitTestApp.Show(element)` puts a control in the host window
so its template, resources and layout apply. It is self-contained like the App (a project that references
a self-contained exe must be self-contained, NETSDK1151). Needs an interactive desktop session; GitHub-hosted
Windows runners should provide one, but a self-hosted runner installed as a service does not.

View-model tests run against the App's real composition root: `Infrastructure/TestApp` builds `AppServices`
through its internal test constructor with `AppServiceFakes` (music services, downloader, uploader, Azure
account service, formatter, an HTTP handler that fails every request, the Documents folder and the legacy
config.json search) over a temporary data root, with every folder setting pointing into it. Export to TTS uses
the real `TtsExporter` writing into a temporary Saved Objects folder. Async `[UITestMethod]`s are supported;
awaiting yields the dispatcher, so `Progress<T>` reports and fire-and-forget loads run (`Wait.UntilAsync`
polls for a condition). What's covered:
- `CollectionBuildTests`: load and retry, missing tools, the build states (running, done, partial failure,
  nothing downloaded, every upload failed, no Azure, signed out, cancel, rebuild).
- `ExportToTtsTests`: Created, Updated, TtsNotFound, Failed and Conflict (Cancel / Replace / Keep both), the
  `ExportResultBar` InfoBars per outcome and its conflict `ContentDialog`.
- `SettingsTests`: every edited setting round-trips through config.json, debounced saving, a save error,
  Test connection results, unconfigured services.
- `AzurePickerTests`: signed out, connection-string fallback, the sign-in cascade to the saved target,
  no storage accounts, grant / ask an owner / public read fixes, sign out, stale loads, create container,
  a restored session.
- `LegacySettingsImportTests`: the first-run config.json import.
- `ConverterTests`: the `Bind` x:Bind functions and `Display` names.

**End to end.** `tests/e2e/ui-smoke.ps1` follows the `winui-ui-testing` batch pattern: one script,
AutomationId selectors, `test-results.json` plus screenshots (one per page and state, and one per failing
check) in `tests/e2e/artifacts/` (gitignored). Every check is required: any failure exits 1. Run it against
Windows Sandbox, not the owner's desktop. It opens every page; in Settings it checks that an unconfigured
service (Spotify) can't be tested, that a failing Azure Test connection shows the error InfoBar
(`AzureTestResult`), and that an edited value is saved to the guest's config.json and survives leaving the page.

With `-DevSource` (Sandbox, a `-p:TtsmDevSource=true` publish with the tools copied in) it also builds the dev
collection and exports it. `guest/setup.ps1` runs in the guest: it moves any existing TTSMixmaster data folder
aside, starts Azurite (the host's node.exe and Azurite, copied in) and a media server on loopback, creates the
`tts-audio` container, and writes config.json and dev-collection.json. The run then checks Test connection
succeeds against Azurite, opens the collection, builds it (real yt-dlp and ffmpeg, upload to Azurite), exports
it to a Saved Objects folder in the guest and finds the build on the Builds page. `guest/teardown.ps1` stops
only the processes setup started and restores the guest's data folder; the script stops only the app it
launched. Nothing reaches a real music service or Azure. Look at the screenshots too: UI Automation can't see
clipping, overlap or theming problems.

## CI wiring (owned by the workflows; not in this change)

1. **Every PR and push** (`windows-latest`): the unit command.
2. **Windows runner, after unit**: fetch-tools into `tests/.tools`, install Azurite (`npm install -g azurite`),
   then the integration command. Publish the TRX.
3. **Windows runner**: the App command with `-p:Platform=x64` (an ARM64 runner would use `-p:Platform=ARM64`).
4. **E2E**: local or Sandbox runs before a release. If feasible in CI, run
   `tests/e2e/ui-smoke.ps1 -Target local -AppExe <publish>\TTSMixmaster.exe` against the published build
   on the Windows runner as a smoke check (needs the winapp CLI on the runner), and upload
   `tests/e2e/artifacts` on failure.
