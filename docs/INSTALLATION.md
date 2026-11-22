# Installation Guide

TTSMixmaster runs on Windows 10 (version 1809 or later) and Windows 11, on x64 and ARM64 PCs.
Everything it needs is included: the .NET runtime, the Windows App SDK, yt-dlp and FFmpeg.

Download it from the [latest release](https://github.com/MWG-Logan/TTSMixmaster/releases/latest).

## Install (recommended): App Installer, with automatic updates

1. Download **`TTSMixmaster.appinstaller`** from the release.
2. Open it. Windows' App Installer shows TTSMixmaster, signed by Morningwood Gaming LLC.
3. Select **Install**. App Installer downloads the right build for your PC (x64 or ARM64).
4. Start TTSMixmaster from the Start menu.

TTSMixmaster then keeps itself up to date: it checks for a new release once a day when you start
it, and in the background, and asks before updating. Updates never block the app from starting.

App Installer ships with Windows 10 and 11. If opening the file does nothing, install or update
[App Installer from the Microsoft Store](https://apps.microsoft.com/detail/9NBLGGH4NNS1).

## Other ways to install

### MSIX package, without automatic updates

Download **`TTSMixmaster-v<version>.msixbundle`** and open it. It's the same signed package for
both architectures. Installed this way it doesn't check for updates; install a newer release the
same way, or open the `.appinstaller` later to switch to automatic updates.

### Portable zip

Download **`TTSMixmaster-v<version>-portable-x64.zip`** (most PCs) or
**`...-portable-arm64.zip`** (Windows on Arm), unzip it anywhere and run `TTSMixmaster.exe`. Nothing
is installed and nothing updates itself; delete the folder to remove it.

### winget

Once TTSMixmaster is published to the Windows Package Manager:

```powershell
winget install MorningwoodGaming.TTSMixmaster
```

### IT and enterprise deployment

Deploy the signed `.msixbundle` like any other MSIX app:

- **Intune:** add it as a *Line-of-business app* (MSIX).
- **Configuration Manager:** create a *Windows app package (\*.appx, \*.appxbundle, \*.msix, \*.msixbundle)* application.
- **Every user on a PC:** provision it, so each user gets it at their next sign-in:
  ```powershell
  Add-AppxProvisionedPackage -Online -PackagePath .\TTSMixmaster-v2.0.0.msixbundle -SkipLicense
  ```

## Uninstall

Open **Settings → Apps → Installed apps**, find **TTSMixmaster** and select **Uninstall**. For the
portable zip, delete its folder.

## Set up the music services

TTSMixmaster works with whichever services you set up; all of them are optional. Enter the keys in
the app's settings (see the [Configuration Guide](CONFIGURATION.md)).

### Last.fm API (for accessing collections)
1. Visit https://www.last.fm/api/account/create
2. Create an API account and get your API key and secret
3. Add your Last.fm username to the configuration
4. **Note**: Last.fm provides "collections" (top tracks, loved tracks, recent tracks) rather than traditional playlists

### YouTube Data API v3 (for accessing playlists)
1. Go to the [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project or select an existing one
3. Enable the YouTube Data API v3
4. Create credentials (API key)
5. **Optional**: Add your YouTube Channel ID for personal playlists access
6. **Note**: Public playlist search works without Channel ID

### Spotify Web API (for accessing playlists)
1. Go to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/)
2. Create a new app
3. Get your Client ID and Client Secret
4. **Optional**: Add your Spotify User ID for personal playlists access
5. **Note**: Public playlist search works without User ID

### Azure Storage (optional)
Uploads go to an Azure Storage account; see the [Azure Setup Guide](AZURE_SETUP.md).

## Build from source

You need the .NET 10 SDK on Windows. From the repository root:

```powershell
dotnet build TTSMixmaster.slnx -p:Platform=x64
dotnet test tests/TTSMixmaster.Core.Tests
```

To build the release artifacts locally (portable zips, MSIX bundle, `.appinstaller`), see
[RELEASING.md](RELEASING.md).

## Next steps

See the [Usage Guide](USAGE.md) for how to use the app, and
[Troubleshooting](TROUBLESHOOTING.md) if something doesn't work.
