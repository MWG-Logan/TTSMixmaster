# TTSMixmaster UI brief

The plan for the WinUI 3 app. It comes from impeccable `shape`, reconciled with `winui-design`'s
app-shape anchors and control map. Where the two disagree, Fluent wins.

> **Assumptions.** `shape` normally interviews the owner first. This brief was written by a
> dispatched worker with no way to ask, so it rests on the task spec, the legacy GUI
> (the deleted Python GUI, in git history at `5467f7d`), its screenshot and the owner's later scope notes (Azure sign-in, Export to TTS,
> chip preview). Correct anything below that's wrong.

## Job and audience

- **Who:** one person, usually the owner or a friend running a TTS game night. They're comfortable on
  a PC but don't want to think about yt-dlp, blobs or Lua. They come back every few weeks with a new playlist.
- **Mode:** Operate. The tool should get out of the way, so we use standard Fluent controls with nothing invented.
- **Success:** a playlist they already have turns into a music player sitting in TTS's Saved Objects,
  using two obvious actions (**Build**, then **Export to TTS**), and they always know what happened to each track.

## What was wrong with the old UI

- Five tabs that just mirrored the internal pipeline (Configuration / Playlists / Download / Upload /
  TTS Format). The user had to walk the pipeline by hand and carry state across tabs in their head.
- Tracks were dumped into a text box with no per-track status. Failures were hidden in a log.
- All settings sat on one undifferentiated form. The Test buttons were separated from the fields
  they tested, and results came back as modal message boxes.
- Getting the result into TTS meant copying Lua by hand.

## Shape (anchor: "settings/config tool" + "dashboard" → `NavigationView` Left)

```
NavigationView (Left, Mica, custom TitleBar with back + pane toggle)
├─ Collections   find a collection → opens Collection page (back button returns)
│    └─ Collection   chip + options, tracks, Build, per-track status, Export to TTS
├─ Builds        finished builds on disk: export status, Export to TTS again, More (Open folder, Copy Lua)
└─ Settings      (footer) Music services, Storage (Azure), Folders and audio, Appearance, About
```

- **Collections** has one input, an `AutoSuggestBox` labelled "Search or paste a link". With the box
  empty, the page lists *your* collections from every connected service. Typing searches.
  Pasting a share URL opens that collection directly. There's a service `ComboBox`
  ("All connected services" / Last.fm / YouTube / Spotify) and a kind `SelectorBar`
  (All · Playlists · Charts). **Albums** goes in later as one more `SelectorBarItem` mapped to
  `CollectionKind.Album`. The list, search and detail code are all keyed on `CollectionKind`, so
  nothing else has to move. Each row leads with the provider's mark (Simple Icons, CC0).
- **Collection** shows a header (provider mark, name, service · kind · N tracks · owner) and one accent
  action, **Build for Tabletop Simulator**. A collapsed `Expander`, "Tabletop object", holds the
  **chip preview** (a dark cartridge, about 184 px, with album/service art and the name in serif small
  caps), a **Show title on chip** toggle, **Use custom image…** (file picker), name, description and image
  URLs. Then comes the track list: a `ListView` with a `Grid` template and a header row
  (# · Title/Artist · Album · Length · Status).
- **Build** runs download → upload → generate. While it runs, a progress card shows
  "Step 2 of 3 · Uploading to Azure · 7 of 18", a determinate `ProgressBar` and **Cancel build**, and
  each row updates its status icon and message live. When it finishes, an `InfoBar` gives the outcome.
- **Put it in Tabletop Simulator** is the output card. **Export to TTS** is the accent action. It
  writes `Saved Objects\TTSMixmaster\<name>.json` (+ `.png`). Next to it are **Open output folder** and a
  **More** (`…`) `MenuFlyout` holding **Copy Lua script**. After a build, the header button steps down
  to a standard **Build again**, so only one accent button is on screen at a time.
- **Builds** lists the builds the app owns (identity-keyed folders, `<Name> [<service>-<hash>]`).
  It opens with a short "How to use in Tabletop Simulator" note: Export to TTS, then Objects → Saved
  Objects → TTSMixmaster and click to place it. Each row shows the service, track count, build time and
  its **status** ("Exported to TTS <time>" / "Not exported yet", icon + words). Its accent action is
  **Export to TTS**, with the same success / conflict / TTS-not-found handling as the Collection page
  (one shared view model and result bar). **Open folder** and **Copy Lua script** sit in the row's `…` menu.
- **Settings** auto-saves (Windows Settings convention, no Save button). Each music service is a
  `SettingsExpander` with its fields, a **Test connection** button and an inline `InfoBar` for the
  result. Secrets use `PasswordBox`. The expander header shows "Not set up" / "Set up, not tested" / "Connected".
- **Storage → Azure Storage** is interactive sign-in with cascading pickers, all inside one
  `SettingsExpander`:
  1. **Account:** **Sign in to Azure** (WAM account picker). Signed in, it shows name · email and a **Sign out** button.
  2. **Directory** (only when the account has more than one) → **Subscription** → **Storage account**
     (name with resource group · region) → **Container** (with a **New container** flyout). Each
     `ComboBox` stays disabled with "Choose X first" until the level above loads, shows "Loading…"
     while it loads, auto-picks a lone item, and preselects the container named in settings.
  3. An **access check** runs on the chosen container. Results are `InfoBar`s below the expander. Every
     fix is a button the user presses; nothing changes in Azure on its own.
  4. **Advanced: use a connection string instead** is a collapsed `SettingsExpander` (expanded when
     sign-in isn't available) with the old connection string `PasswordBox`, container and Test connection.
- **Folders and audio** adds **Tabletop Simulator saved objects**, which shows where Export writes and
  offers **Change folder** (`FolderPicker` by window id) and **Use default**.

## States

| State | What the user sees |
|---|---|
| First run (no service set up) | Collections shows "Connect a music service" with an **Open Settings** button. |
| Loading collections / tracks | `ProgressRing` in place of the list. Header and filters stay usable. |
| No results | "No collections match "…"." Clear search is one keystroke away (the box's ×). |
| Service error | Warning `InfoBar` above the list naming the service. Other services' results still show. |
| Azure not set up | Build stays enabled. The Collection page warns "Set up Azure Storage before building", with **Open Settings**. |
| Build running | Progress card + per-track status. The header shows **Cancel build**. Navigating away doesn't cancel the build. |
| Partial failure | Warning `InfoBar`: "15 of 18 tracks are ready. 3 tracks were left out of the player…" Failed rows show their reason inline. |
| Cancelled | Informational `InfoBar`: "Build cancelled. Nothing was generated." |
| Exported | Success `InfoBar`: "In Tabletop Simulator, open Objects → Saved Objects → TTSMixmaster → <name>." with **Show in folder**. |
| Export conflict | `ContentDialog` "Replace the saved object?" with **Replace** / **Keep both** / **Cancel** (default). |
| TTS not found | Warning `InfoBar` "Couldn't find Tabletop Simulator", which explains where it looked, with **Open Settings** (the saved-objects folder row). |
| Azure signed out | "Not signed in". The Account row shows **Sign in to Azure**. |
| Sign-in unavailable (no client ID in this build) | **Sign in** is disabled and the Account row explains why. The connection-string expander opens by default. |
| Azure picking | Cascading pickers with "Choose X first" / "Loading…" placeholders. No storage accounts → informational `InfoBar` with **Open the Azure portal**. |
| Needs role, can grant | Warning "You can't upload here yet" with **Grant me access** (container scope only). |
| Waiting for role | Informational "Access granted, waiting for Azure" with an indeterminate bar. It re-checks every 5 s. |
| Needs role, can't grant | Error "Ask an owner for access", naming the role, with **Copy role name** and **Check again**. |
| Not anonymously readable | Warning "Tabletop Simulator can't read these files yet" with **Allow public read access**. One line says what it means, including that it re-enables other public containers on the account. |
| All good | Success "Ready to upload: builds upload to account/container, and Tabletop Simulator can stream from it." |
| Builds empty | "No builds yet", with **Browse collections**. |
| Build not exported | Row status "Not exported yet"; **Export to TTS** on the row. |

## Copy rules

- Use verbs on buttons: Build, Export to TTS, Test connection, Grant me access, Open output folder.
- Errors say what happened and what to do next. Don't use "Error:" prefixes or exclamation marks.
- Say "collection" generically and "playlist" / "chart" / "album" when the kind is known.

## Constraints

- Build against `TTSMixmaster.Core` only. `Services.cs` is the one composition root that constructs the Core
  services (the design-time fakes were removed at integration; `-p:TtsmDevSource=true` compiles a local test
  collection for credential-free verification).
- Use `x:Bind` everywhere and an AutomationId on every interactive control. Support Light, Dark and
  System themes and leave High Contrast to the system brushes. The one exception is the chip preview,
  which depicts a physical cartridge in fixed colours.
- Default window size is 1200×800 DIP (clamped to the screen's work area and centred), minimum
  720×560. Below 1000 px the nav pane collapses to compact.

## Integration notes

Done: the Azure account/container, the saved-objects folder and the chip options persist through Core's settings
keys, and the access fix uses Core's `WaitForAccessAsync`. See `docs/ui/REVIEW.md` and `docs/testing/LIVE-CHECKLIST.md`.
