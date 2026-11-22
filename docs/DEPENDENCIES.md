# Dependencies

The owner's rule: use first-party (Microsoft) libraries wherever one fits. Community packages are fine
for image processing and media/provider tooling, and each one that ships in the app is listed here with
why there's no first-party option.

Microsoft packages (`Microsoft.*`, `System.*`, `Azure.*`, `runtime.*`) aren't listed. The list below
is every other package in the published app's `TTSMixmaster.deps.json` (checked 2026-11-21).

## Community packages that ship

| Package | Version | Maintainer | Why no first-party option | License |
|---|---|---|---|---|
| TagLibSharp | 2.3.0 | mono/taglib-sharp community | Core writes and reads a custom ID3 `TXXX:TTSMIXMASTER_TRACK_KEY` frame that the library layout uses to verify a track file (ADR 0001). Windows' `MusicProperties` / property system only exposes the standard fields (title, artist, album, …) and can't write arbitrary TXXX frames; Core is also plain `net10.0` with no WinRT. | LGPL-2.1 |
| Apache.Arrow | 22.1.0 | Apache Software Foundation | Not chosen by us: a transitive dependency of Microsoft's `Azure.Storage.Blobs` 12.30 (blob query Arrow output). Unused by the app's code. | Apache-2.0 |

## Community-hosted but Microsoft-maintained

Treated as first-party: Microsoft maintains and signs them.

| Package | Version | Maintainer | Use | License |
|---|---|---|---|---|
| SkiaSharp, SkiaSharp.NativeAssets.Win32 | 4.157.0 | Microsoft (mono/SkiaSharp) | Chip image rendering (`Core/Imaging`). There's no first-party .NET image library for a `net10.0` class library; System.Drawing is Windows-only and not recommended for new code. | MIT |
| SkiaSharp.HarfBuzz, HarfBuzzSharp, HarfBuzzSharp.NativeAssets.Win32 | 4.157.0 / 14.4.0.200 | Microsoft (mono/SkiaSharp) | Text shaping for the chip title (kerning, complex scripts, emoji). | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | Microsoft / .NET Foundation | `[ObservableProperty]`, `[RelayCommand]` source generators. | MIT |
| CommunityToolkit.WinUI.Controls.SettingsControls (+ .Extensions, .Helpers, .Triggers, CommunityToolkit.Common) | 8.2.251219 / 8.2.1 | Microsoft / .NET Foundation | `SettingsCard` / `SettingsExpander`, the Windows Settings look the WinUI guidance recommends. | MIT |

## External tools (not NuGet)

`yt-dlp.exe` and `ffmpeg.exe` / `ffprobe.exe` ship next to the app (`scripts/fetch-tools.ps1` pins and
verifies them). Media/provider tooling, so community is expected. Settings > **Update yt-dlp** installs a
newer yt-dlp into `%LOCALAPPDATA%\TTSMixmaster\tools` after checking the release's published SHA-256.

## Reviewed and kept

No community package was found that a first-party API covers cleanly. The Windows App SDK metapackage's
unused components (AI, ML/onnxruntime/DirectML, Search, Widgets, DWriteCore) are excluded in
`TTSMixmaster.App.csproj`, so they don't ship.
