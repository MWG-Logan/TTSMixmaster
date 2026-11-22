# UI review: critique, code review, and the impeccable verdict

This covers the WinUI 3 overhaul on `dotnet/winui-port`. Evidence is the source plus Windows Sandbox
screenshots of every page and state in light and dark (curated set in `screenshots/`).

## Design critique (impeccable `critique`, Assessment A only)

Method: design review only. impeccable's detector (Assessment B) reads web markup only, and the
critique flow lists `ios` / `android` / `adaptive` as its native platforms, not Windows desktop. Scored
against impeccable's rubric (Nielsen's heuristics, cognitive load, personas) with Fluent conventions
taking precedence.

| # | Heuristic | Score | Key finding |
|---|---|---|---|
| 1 | Visibility of system status | 4 | Per-track live status, "Step 2 of 3 · 7 of 18", Azure "waiting for role", status text in every expander header. |
| 2 | Match system / real world | 3 | Plain language throughout. Azure's own nouns (directory, subscription, container, role) are unavoidable, but each fix explains itself. |
| 3 | User control and freedom | 3 | Cancel build, Cancel as the default in dialogs, Sign out, Use default. **Was 2:** "Allow public read access" changed an account-wide setting in one click (fixed, see P1). |
| 4 | Consistency and standards | 4 | Stock Fluent controls only: NavigationView, SettingsCard/Expander, InfoBar, ContentDialog, MenuFlyout, SelectorBar. One accent button per screen. |
| 5 | Error prevention | 3 | Disabled cascade pickers with "Choose X first", container-name validation, conflict dialog. A build without Azure still downloads first (deliberate: the audio is kept). |
| 6 | Recognition over recall | 3 | Provider marks, selections remembered for the session. The Azure target and TTS folder aren't persisted until Core's settings keys are wired in. |
| 7 | Flexibility and efficiency | 2 | **Was 1.** No shortcuts at all; F5 now refreshes (P2). No batch build of several collections. |
| 8 | Aesthetic and minimalist design | 3 | Calm and native. After a build, result + output card + options push the track list down. That's acceptable because that's where attention goes next. |
| 9 | Error recovery | 4 | Every error says what happened and offers the next step inline (Open Settings, Grant me access, Copy role name, Try again). |
| 10 | Help and documentation | 2 | Help is inline copy only. Where-to-find-your-key hints are text, not links. |
| | **Total** | **31/40** | **Good** |

**Personas.**
- *First-timer (Jordan):* first run points straight to Settings and the Collection page warns before an Azure-less build. Sign-in replaces pasting a connection string.
- *Power user (Alex):* wants batch builds and more shortcuts.
- *Accessibility (Sam):* AutomationIds and names everywhere. Track status is icon + text, never colour alone. Build progress is a live region. List items have real names.
- *Stress tester (Riley):* pasting a non-music link gives a clear error. A pasted link for a disconnected service says which service to connect. Changing pickers mid-load drops stale results.

**Priority issues and what happened.**
- **P1, fixed:** "Allow public read access" now asks first (`ContentDialog`, Cancel default) and
  spells out the account-wide side effect.
- **P2, fixed:** F5 on Collections and Builds refresh.
- **P2, open:** turn the "get a key at …" descriptions into `HyperlinkButton`s.
- **P3, open:** batch builds; persisting the Azure target, the TTS folder and the chip options (Core settings keys).
- **P3, open:** the app icon is still the template placeholder.

## Code review (winui-code-review)

The WinUI analyzer (`Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer` 0.8.0) is clean: 0 warnings.
Fixed during review:

| Finding | Severity | Fix |
|---|---|---|
| `DefaultSettingsCardStyle` didn't exist (a guessed key) and crashed Settings at load. Found via the new crash log. | Error | Removed the `BasedOn` style. |
| ListView/ComboBox items had class-name UI Automation names (`ToString`). | Warning | `ToString` overrides on row view models and Azure records. |
| `AutomationId` / `LiveSetting` on `Border` (no automation peer), so the build progress wasn't announced. | Warning | Moved to the step `TextBlock` and real controls. |
| `async void` on a non-event method (`ScheduleSave`). | Warning | Returns a `Task`, discarded at the call site. |
| Closed InfoBars still took StackPanel spacing (36 px gap under Azure). | Note | Collapse with the same flag as `IsOpen`. |
| Closable InfoBars bound OneWay to computed flags couldn't reopen. | Note | Made non-closable; they clear on the next action. |
| Nested x:Bind paths (WUI2010) and OneTime defaults (WUI2011). | Warning | Shared `DataTemplate`s per connection; explicit modes. |
| Window could be taller than the screen (Sandbox: 823 px). | Warning | Clamped to the work area and centred. |

Known and accepted:
- `InfoBarSeverity` in view models: a plain enum, no visual tree.
- The chip preview uses fixed colours: it depicts a physical cartridge until Core's renderer supplies the bitmap.
- User-facing strings aren't in `.resw` yet. The app was English-only before too, and moving them is mechanical work for later.

## Was `impeccable` worth it for WinUI?

**Keep it, but only for planning and critique, and invoke those directly.** Don't run `impeccable` itself on this project.

- **Helped:** `shape` set the structure: job, mode ("Operate: the tool disappears into the task"),
  states and copy rules before any XAML existed. That's where the "one input for search *and* pasted
  links" and the state table came from. The critique rubric (heuristics + personas) surfaced the two
  fixes above that the code review and analyzer can't see. `clarify`/`harden`-style copy discipline
  shaped every error message.
- **Inapplicable or got in the way:**
  - The context step requires `PRODUCT.md`/`DESIGN.md` at the repo root plus an owner interview. A worker can do neither.
  - The detector, live mode, `shot` tooling and the craft floor are web-only.
  - The critique flow doesn't recognise Windows desktop as native, so it asks for a browser and a detector that can't read XAML.
  - Its visual-world playbooks (custom fonts, "own visual world") conflict with Fluent, as `AGENTS.md` already warns.
- **Recommendation:** keep it in the toolchain for `shape` and `critique` (Assessment A only), and for
  copy work (`clarify`, `harden`, `onboard`), invoked directly. `winui-design` stays the authority on
  controls, layout and theming.
