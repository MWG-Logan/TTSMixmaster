# TTSMixmaster UI brief

The plan for the WinUI 3 app. It comes from impeccable `shape`, reconciled with `winui-design`'s
app-shape anchors and control map. Where the two disagree, Fluent wins.

> **Assumptions.** `shape` normally interviews the owner first. This brief was written by a
> dispatched worker with no way to ask, so it rests on the task spec, the legacy GUI
> (`src/gui/*.py`) and its screenshot. Correct anything below that's wrong.

## Job and audience

- **Who:** one person, usually the owner or a friend running a TTS game night. They're comfortable on
  a PC but don't want to think about yt-dlp, blobs or Lua. They come back every few weeks with a new playlist.
- **Mode:** Operate. The tool should get out of the way, so we use standard Fluent controls with nothing invented.
- **Success:** a playlist they already have turns into a working TTS music player with one main
  action ("Build"), and they always know what happened to each track.

## What was wrong with the old UI

- Five tabs that just mirrored the internal pipeline (Configuration / Playlists / Download / Upload /
  TTS Format). The user had to walk the pipeline by hand and carry state across tabs in their head.
- Tracks were dumped into a text box with no per-track status. Failures were hidden in a log.
- All settings sat on one undifferentiated form. The Test buttons were separated from the fields
  they tested, and results came back as modal message boxes.

## Shape (anchor: "settings/config tool" + "dashboard" → `NavigationView` Left)

```
NavigationView (Left, Mica, custom TitleBar)
├─ Collections   find a collection → opens Collection page (back button returns)
│    └─ Collection   tracks + Build + per-track status + output actions
├─ Builds        finished builds on disk: copy Lua, open folder, how to load in TTS
└─ Settings      (footer) SettingsExpander per service, Storage, Folders & audio, Appearance, About
```

- **Collections** has one input, an `AutoSuggestBox` labelled "Search or paste a link". With the box
  empty, the page lists *your* collections from every connected service. Typing searches.
  Pasting a share URL opens that collection directly. There's a service `ComboBox`
  ("All connected services" / Last.fm / YouTube / Spotify) and a kind `SelectorBar`
  (All · Playlists · Charts). **Albums** goes in later as one more `SelectorBarItem` mapped to
  `CollectionKind.Album`. The list, search and detail code are all keyed on `CollectionKind`, so
  nothing else has to move.
- **Collection** shows a header (name, service · kind · N tracks · owner) and an accent
  **Build for Tabletop Simulator** button. Below that is a collapsed `Expander`
  "Tabletop object" (nickname, description, two image URLs), then the track list: a `ListView` with a
  `Grid` template and a header row (# · Title/Artist · Album · Length · Status).
- **Build** runs download → upload → generate. While it runs, a progress card shows
  "Step 2 of 3 · Uploading to Azure · 7 of 18", a determinate `ProgressBar` and **Cancel**, and each
  row updates its status icon and message live. When it finishes, an `InfoBar` gives the outcome and an
  output card offers **Copy Lua script**, **Open output folder** and the three-step "Load it in TTS" text.
- **Settings** auto-saves (Windows Settings convention, no Save button). Each service is a
  `SettingsExpander` with its fields, a **Test connection** button and an inline `InfoBar` for the
  result. Secrets use `PasswordBox`. The expander header shows "Connected" / "Not set up".

## States

| State | What the user sees |
|---|---|
| First run (no service set up) | Collections shows an empty state: "Connect a music service to find your playlists." with a **Open Settings** button. |
| Azure not set up | Build stays enabled. The Collection page shows a warning `InfoBar`: "Uploading needs Azure Storage. Set it up in Settings." with an action button. |
| Loading collections / tracks | `ProgressRing` in place of the list. Header and filters stay usable. |
| No results | "No collections match "…"." Clear search is one keystroke away (the box's ×). |
| Service error | Error `InfoBar` above the list naming the service. Other services' results still show. |
| Build running | Progress card + per-track status. The Build button becomes **Cancel**. Navigating away doesn't cancel the build. |
| Partial failure | Warning `InfoBar`: "16 of 18 tracks are ready. 2 couldn't be found. They're left out of the player." Failed rows show their reason inline. |
| Cancelled | Informational `InfoBar`: "Build cancelled. Nothing was generated." |
| Done | Success `InfoBar` + output card. |
| Builds empty | "Finished builds show up here. Open a collection and choose Build." |

## Copy rules

- Use verbs on buttons: Build, Test connection, Copy Lua script, Open output folder.
- Errors say what happened and what to do next. Don't use "Error:" prefixes or exclamation marks.
- Say "collection" generically and "playlist" / "chart" / "album" when the kind is known.

## Constraints

- Build against the `TTSMixmaster.Core` contracts only. The fakes live in `App/DesignTime/`, and
  `Services.cs` is the one composition root where the real implementations get swapped in.
- Use `x:Bind` everywhere and an AutomationId on every interactive control. Support Light, Dark and
  System themes and leave High Contrast to the system brushes (no hard-coded colours).
- Default window size is 1200×800 DIP, minimum 720×560. Below 1000 px the nav pane collapses to compact.
