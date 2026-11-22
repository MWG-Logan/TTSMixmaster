# Testing

All .NET tests use **MSTest** (`MSTest.Sdk`) on **Microsoft.Testing.Platform** (MTP). Don't add xUnit or NUnit.
`global.json` pins the SDK band, the `MSTest.Sdk` version and selects MTP for `dotnet test`. Bump
`MSTest.Sdk` by hand in `global.json`: Dependabot and Visual Studio don't update MSBuild SDK versions.

The layers follow Microsoft's guidance for WinUI 3 desktop apps: fast unit tests for the UI-free Core,
integration tests against real tools and emulators, UI-thread tests in a self-hosted WinUI test app,
and a few end-to-end UI Automation checks against the running app.

| Layer | Project / script | What it covers | Needs | Where it runs |
|---|---|---|---|---|
| Unit | `tests/TTSMixmaster.Core.Tests` | Core logic with fakes (HTTP, processes, clock, folders) | Windows | Every PR and push |
| Integration | `tests/TTSMixmaster.Core.IntegrationTests` (`[TestCategory("Integration")]`) | Real filesystem, real yt-dlp/ffmpeg against a loopback media server, uploader against Azurite, formatter/exporter writing into an injected TTS root | `tests/.tools` (fetch-tools), Azurite; Inconclusive without them | Windows runner, after fetch-tools |
| App (UI thread) | `tests/TTSMixmaster.App.Tests` | View models, converters and controls on the WinUI dispatcher (`[UITestMethod]`) | Windows App SDK (self-contained), an interactive session | Windows runner |
| End to end | `tests/e2e/ui-smoke.ps1` | The running app through UI Automation (`winapp ui`), AutomationId selectors | winapp CLI, Windows Sandbox (or local desktop) | Locally / Sandbox; optionally against the published build |

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

# End to end (local, against Windows Sandbox; see AGENTS.md)
tests/e2e/ui-smoke.ps1 -Target sandbox
tests/e2e/ui-smoke.ps1 -Target local -AppExe <publish folder>\TTSMixmaster.exe   # smoke the published build
```

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

Today it holds seed tests (the host dispatcher, Fluent resources, App assembly identity). Once the App UI
lands, add:
- view-model tests for each page's view model (commands, `CanExecute`, validation, progress and
  cancellation), with Core seams faked;
- converter tests (status to glyph/visibility/text);
- control tests through `UnitTestApp.Show(...)` for custom controls and templated items;
- a composition test that resolves the App's services/view models the way `App` does at startup.

**End to end.** `tests/e2e/ui-smoke.ps1` follows the `winui-ui-testing` batch pattern: one script,
AutomationId selectors, `test-results.json` plus screenshots (initial, final, and one per failing check)
in `tests/e2e/artifacts/` (gitignored). Run it against Windows Sandbox, not the owner's desktop.
App-specific checks still use `TODO-SELECTOR-*` placeholders and report PENDING: replace them with the
AutomationIds from `docs/ui/BRIEF.md` once the UI is merged, then remove them from `$PendingChecks`. Look at
the screenshots too: UI Automation can't see clipping, overlap or theming problems.

## CI wiring (owned by the workflows; not in this change)

1. **Every PR and push** (`windows-latest`): the unit command.
2. **Windows runner, after unit**: fetch-tools into `tests/.tools`, install Azurite (`npm install -g azurite`),
   then the integration command. Publish the TRX.
3. **Windows runner**: the App command with `-p:Platform=x64` (an ARM64 runner would use `-p:Platform=ARM64`).
4. **E2E**: local or Sandbox runs before a release. If feasible in CI, run
   `tests/e2e/ui-smoke.ps1 -Target local -AppExe <publish>\TTSMixmaster.exe` against the published build
   on the Windows runner as a smoke check (needs the winapp CLI on the runner), and upload
   `tests/e2e/artifacts` on failure.
