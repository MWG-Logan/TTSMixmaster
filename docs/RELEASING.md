# Releasing TTSMixmaster

Releases are built, signed and published by
[`.github/workflows/build-and-release.yml`](../.github/workflows/build-and-release.yml).
The only thing you change by hand is the version.

## Cut a release

1. Bump `<Version>` in [`Directory.Build.props`](../Directory.Build.props). Use plain
   `MAJOR.MINOR.PATCH` (for example `2.1.0`). MSIX versions can't carry a pre-release suffix, so
   the workflow rejects one.
2. Merge to `main`.

On every push to `main` the workflow reads `<Version>`:

- If tag `v<Version>` already exists, it stops. Nothing is built, tagged or released, so
  ordinary merges that don't bump the version are no-ops.
- Otherwise it runs the Core tests (a failure blocks the release), builds and signs everything,
  then creates tag `v<Version>` on the pushed commit and publishes the GitHub release as
  **Latest**. Installed copies update from the latest release (see [Auto-update](#auto-update)),
  so publishing it is what ships the update.

The release notes are a fixed downloads table followed by GitHub's generated notes, anchored on
the previous `v*` tag.

To rebuild an existing version (for example after a signing hiccup), run the workflow manually
(**Actions → Build and Release TTSMixmaster → Run workflow**) with **force_release** ticked, on the
commit the tag `v<Version>` points at. It rebuilds that commit and replaces the assets on the existing
release. Tags are treated as immutable: if the branch has moved past the tag, the run fails and asks
you to bump `<Version>` instead, because different code under the same version would never reach
installed copies (App Installer only updates to a higher version) and the tag would no longer describe
the assets.

## What a release contains

| File | What it is |
|---|---|
| `TTSMixmaster.appinstaller` | **The recommended download.** A small XML file that App Installer opens to install the bundle below and keep it updated. |
| `TTSMixmaster-v<ver>.msixbundle` | The signed MSIX bundle with the x64 and ARM64 packages. Windows installs the one that fits the PC. Also what winget, Intune and Configuration Manager deploy. |
| `TTSMixmaster-v<ver>-portable-x64.zip` | The self-contained x64 publish folder. Unzip and run `TTSMixmaster.exe`. |
| `TTSMixmaster-v<ver>-portable-arm64.zip` | The same for ARM64. |

Every package contains the unpackaged, self-contained (.NET and Windows App SDK) publish folder
of `src/TTSMixmaster.App` for its architecture, plus native builds of `yt-dlp.exe`, `ffmpeg.exe`
and `ffprobe.exe` for that architecture. [`scripts/fetch-tools.ps1`](../scripts/fetch-tools.ps1)
downloads those tools from pinned URLs and checks them against SHA-256 hashes: yt-dlp's
`yt-dlp.exe`/`yt-dlp_arm64.exe`, gyan.dev's FFmpeg 8.0.1 essentials build for x64, and a
month-end BtbN `winarm64` GPL build for ARM64 (BtbN keeps those for two years).

There is no MSI. Enterprise deployment uses the same MSIX:

- **Intune:** add the `.msixbundle` as a *Line-of-business app*.
- **Configuration Manager:** create a *Windows app package* application from it.
- **All users on a PC:** `Add-AppxProvisionedPackage -Online -PackagePath TTSMixmaster-v<ver>.msixbundle -SkipLicense`
  provisions it for every user at their next sign-in.

### How it's built

[`scripts/package-release.ps1`](../scripts/package-release.ps1) does the work in stages, so CI can
sign between them. Locally, with no arguments, it builds everything unsigned for both
architectures:

```powershell
./scripts/package-release.ps1
# -> artifacts/release/  (bundle, .appinstaller, zips); per-arch .msix in artifacts/msix/
```

| Stage | Runs | Does | CI then signs |
|---|---|---|---|
| `Publish` | per arch | `dotnet publish` the App, add that arch's yt-dlp/ffmpeg | `TTSMixmaster.exe`, `TTSMixmaster.dll`, `TTSMixmaster.Core.dll` |
| `Package` | per arch | portable zip, unsigned `.msix` | (nothing: the zip already has the signed binaries) |
| `Bundle` | once | `.msixbundle` from both `.msix` files (`makeappx bundle`), plus `TTSMixmaster.appinstaller` | the `.msixbundle` |

In CI the `build` job runs `Publish` and `Package` for x64 and ARM64 in parallel, and the `bundle`
job combines them. Third-party binaries (yt-dlp, FFmpeg, the .NET and Windows App SDK runtime
files) are never re-signed. CI checks the signatures with `signtool verify /pa`.

[`installer/msix/`](../installer/msix) holds `Package.appxmanifest`, its icons and
`priconfig.xml`. The script stamps `Version` (`X.Y.Z.0`) and `ProcessorArchitecture` into a copy,
builds a package layout from the publish folder and runs `winapp package` on it. The app is built
unpackaged, so its XAML resources live in `TTSMixmaster.pri`, but a packaged process reads
`resources.pri`. The script therefore merges `TTSMixmaster.pri` and the icons into the package's
`resources.pri` with makepri (`priconfig.xml`). Without that, the MSIX installs but crashes on
launch. The app stays self-contained inside the MSIX, so the package has no framework
dependencies.

## Auto-update

`TTSMixmaster.appinstaller` (generated by the `Bundle` stage) looks like this:

```xml
<AppInstaller Version="2.0.0.0"
    Uri="https://github.com/MWG-Logan/TTSMixmaster/releases/latest/download/TTSMixmaster.appinstaller"
    xmlns="http://schemas.microsoft.com/appx/appinstaller/2018">
  <MainBundle Name="MorningwoodGaming.TTSMixmaster" Publisher="CN=Morningwood Gaming LLC, ..."
      Version="2.0.0.0"
      Uri="https://github.com/MWG-Logan/TTSMixmaster/releases/download/v2.0.0/TTSMixmaster-v2.0.0.msixbundle" />
  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="24" ShowPrompt="true" UpdateBlocksActivation="false" />
    <AutomaticBackgroundTask />
  </UpdateSettings>
</AppInstaller>
```

- **`Uri`** is the stable address installed copies poll. GitHub's `releases/latest/download/<asset>`
  redirects to that asset on the newest release marked **Latest**. Don't rename the
  `.appinstaller` asset, and keep new releases marked Latest (the workflow does this).
- **`MainBundle/@Uri`** points at that release's own `.msixbundle`, so each `.appinstaller` names
  exactly the bundle it was built with.
- **Updates:** checked when the app starts, at most once every 24 hours, and by Windows' background
  task (about every 8 hours). They never block the app from starting. An update found while the
  app runs is downloaded and staged, then applied the next time the app starts.

Only installs made by opening the `.appinstaller` update themselves. A `.msixbundle` opened
directly, winget, Intune and Configuration Manager installs don't. winget and the management
tools have their own update channels.

### Verified behaviour (Windows Sandbox, dev-signed bundles)

App Installer was tested against a local server that copies GitHub's download chain:
`latest/download` → 302 → `releases/download/<tag>/<file>` → 302 to a *different host* → 200
`application/octet-stream` with `Content-Disposition: attachment` and byte ranges. GitHub returns
the same chain (checked with `curl -IL` against a real release). Results:

- `Add-AppxPackage -AppInstallerFile <latest/download URL>` followed both redirects, installed
  2.0.0, and registered the stable `latest/download` URL as the package's `AppInstallerUri`
  (`Get-AppPackageAutoUpdateSettings`).
- It downloaded only the x64 payload of the two-architecture bundle (byte ranges, about 211 MB of
  432 MB), so the combined bundle doesn't double the download.
- After "publishing" 2.0.1 (changing what `latest` redirects to) and starting the app, App
  Installer fetched the new `.appinstaller`, staged 2.0.1 (a small block-level download, since
  little had changed), and installed it the next time the app started. The update settings then
  came from the 2.0.1 file.
- For the test, the first install used `HoursBetweenUpdateChecks="0"` so it didn't have to wait a
  day. With `ShowPrompt="true"` no prompt appeared in the Sandbox; the update went through
  silently instead.

Canonical's Ubuntu Pro for WSL ships the same `releases/latest/download/*.appinstaller` scheme
from GitHub.

## Required secrets and variables

Set these under **Settings → Secrets and variables → Actions**.

| Name | Kind | Used for |
|---|---|---|
| `AZURE_CLIENT_ID` | secret | `azure/login` (OIDC). App registration with a federated credential for this repo's `main` branch (subject `repo:<owner>/TTSMixmaster:ref:refs/heads/main`). |
| `AZURE_TENANT_ID` | secret | `azure/login` |
| `AZURE_SUBSCRIPTION_ID` | secret | `azure/login` |
| `AZURE_TRUSTED_SIGNING_ENDPOINT` | secret | Trusted Signing account endpoint, e.g. `https://eus.codesigning.azure.net/` |
| `AZURE_TRUSTED_SIGNING_ACCOUNT` | secret | Trusted Signing account name |
| `AZURE_CERTIFICATE_PROFILE_NAME` | secret | Certificate profile name |

The app registration needs the **Trusted Signing Certificate Profile Signer** role on the
certificate profile. The federated credential is bound to `main`, so a manual run from another
branch fails at Azure login unless you add a credential for that branch.

### MSIX publisher

`Identity/@Publisher` in `installer/msix/Package.appxmanifest` is
`CN=Morningwood Gaming LLC, O=Morningwood Gaming LLC, L=Farmington Hills, S=Michigan, C=US`, the
subject of the Trusted Signing certificate. If it doesn't match the certificate exactly (same
order, same spacing), signing the bundle fails. Trusted Signing renews the certificate often but
keeps the subject; if the subject ever changes, update the manifest. The `.appinstaller` copies
the publisher from the manifest. To check it against a signed file:

```powershell
(Get-AuthenticodeSignature .\TTSMixmaster.exe).SignerCertificate.Subject
```

## winget (optional)

[`.github/workflows/winget.yml`](../.github/workflows/winget.yml) uses Microsoft's
[wingetcreate](https://github.com/microsoft/winget-create) (pinned version, SHA-256-checked) to open a
PR against `microsoft/winget-pkgs` that adds the new version. Until you opt in it skips.

The release workflow calls it as its last job (a reusable workflow, `workflow_call`) after it creates a
new release; a `force_release` re-upload doesn't resubmit. It can't simply listen for `release: published`:
GitHub doesn't start workflows for events created with the workflow's own `GITHUB_TOKEN`, and the release is
created with that token. No extra PAT is needed just to trigger it.

It submits the signed **`.msixbundle`**, the same package the `.appinstaller` installs; winget
installs, upgrades and removes MSIX packages natively. The manifest has one installer entry per
architecture, both pointing at the bundle, so the workflow passes the URL twice with `|x64` and
`|arm64` overrides.

`wingetcreate update` only adds versions to a package that already exists, so the first
submission is manual:

1. Publish a release with this pipeline.
2. On your PC, install wingetcreate (`winget install Microsoft.WingetCreate`) and create a
   **classic** GitHub token with the `public_repo` scope.
3. Create the package and submit it:
   ```powershell
   wingetcreate new https://github.com/MWG-Logan/TTSMixmaster/releases/download/v2.0.0/TTSMixmaster-v2.0.0.msixbundle
   ```
   Use `MorningwoodGaming.TTSMixmaster` as the identifier, make sure the manifest lists the bundle
   for both `x64` and `arm64`, answer the metadata prompts (publisher, MIT license, short
   description, homepage), and let it submit with your token. It forks `winget-pkgs` under your
   account if needed.
4. Wait for the winget moderators to merge the PR.
5. Opt in:
   - secret `WINGET_TOKEN`: a classic PAT with `public_repo` scope, from the account that owns the
     `winget-pkgs` fork;
   - variable `WINGET_ENABLED` = `true`;
   - variable `WINGET_PACKAGE_ID`, only if you used a different identifier than
     `MorningwoodGaming.TTSMixmaster`.

From then on every new release opens a winget PR automatically. To submit an existing release
again, run **Publish to winget** manually with its tag (for example `v2.0.0`).

## Tooling and dependency policy

- **First-party wherever one fits.** The workflows use only `actions/*`, `azure/*` and
  `microsoft/*` actions, plus Microsoft's `wingetcreate` for winget. The MSIX tooling is
  Microsoft's (`winapp`, `makeappx`, `makepri`, `signtool`). There's no WiX: Windows Installer
  packages aren't built at all.
- **Pinned.** Every action is pinned to a full commit SHA with a version comment (for example
  `azure/artifact-signing-action@c7ab2a8… # v2.0.0`), so a moved tag can't change what signs the
  release. Downloaded tools (wingetcreate, yt-dlp, FFmpeg) are pinned by URL and SHA-256.
- **Updated by Dependabot.** `.github/dependabot.yml` asks for weekly NuGet updates (minor and
  patch grouped into one PR) and weekly GitHub Actions updates, which bump the pinned SHA and its
  comment together. Bump FFmpeg pins in `scripts/fetch-tools.ps1` by hand; yt-dlp is below.

## Keeping yt-dlp current

When YouTube changes and downloads start failing, there are two fixes, and they work together:

- **Users, right away:** Settings → **Update yt-dlp** installs the latest yt-dlp into
  `%LOCALAPPDATA%\TTSMixmaster\tools`, which is used instead of the bundled copy. It checks the release's
  SHA-256 first ([trust model](DEPENDENCIES.md#trust-model-of-update-yt-dlp)). No app release is needed.
- **Maintainers, for everyone:** update the pinned yt-dlp version and both SHA-256s (`yt-dlp.exe` for x64,
  `yt-dlp_arm64.exe` for ARM64, from the release's `SHA2-256SUMS`) in `scripts/fetch-tools.ps1`, bump the
  patch `<Version>`, and merge to `main`. The release ships the new copy, and App Installer delivers it to
  installed copies, so new installs don't start with a stale yt-dlp.

## Testing artifacts locally

The owner's machine has a Defender ASR rule that blocks new unsigned executables outside excluded
folders. Copy unsigned local builds into `artifacts-local/` in the main checkout (excluded and
git-ignored) before running them, and test installs in Windows Sandbox. To install the unsigned
bundle, sign it with a throwaway certificate that has the same subject as the manifest:

```powershell
winapp cert generate --output devcert.pfx --valid-days 7 `
  --publisher 'CN=Morningwood Gaming LLC, O=Morningwood Gaming LLC, L=Farmington Hills, S=Michigan, C=US'
winapp sign artifacts/release/TTSMixmaster-v2.0.0.msixbundle devcert.pfx --password <its password>
# On the test machine: import the certificate into LocalMachine\TrustedPeople, then
# Add-AppxPackage TTSMixmaster-v2.0.0.msixbundle
```

To test auto-update, build the `.appinstaller` against a local URL with
`./scripts/package-release.ps1 -Stage Bundle -ReleasesUrl http://localhost:8080/<owner>/<repo>/releases`
and serve the files from a local HTTP server.
