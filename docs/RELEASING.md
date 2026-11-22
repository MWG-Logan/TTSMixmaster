# Releasing TTSMixmaster

Releases are built, signed and published by
[`.github/workflows/build-and-release.yml`](../.github/workflows/build-and-release.yml).
The only thing you change by hand is the version.

## Cut a release

1. Bump `<Version>` in [`Directory.Build.props`](../Directory.Build.props). Use plain
   `MAJOR.MINOR.PATCH` (for example `2.1.0`). MSI and MSIX versions can't carry a pre-release
   suffix, so the workflow rejects one.
2. Merge to `main`.

On every push to `main` the workflow reads `<Version>`:

- If tag `v<Version>` already exists, it stops. Nothing is built, tagged or released, so
  ordinary merges that don't bump the version are no-ops.
- Otherwise it runs the Core tests (a failure blocks the release), builds and signs every
  artifact for x64 and ARM64, then creates tag `v<Version>` on the pushed commit and publishes
  the GitHub release.

The release notes are a fixed downloads table followed by GitHub's generated notes, anchored on
the previous `v*` tag.

To rebuild an existing version (for example after a signing hiccup), run the workflow manually
(**Actions → Build and Release TTSMixmaster → Run workflow**) with **force_release** ticked. It
rebuilds from the branch you pick and replaces the assets on the existing release. It doesn't
move the tag.

## What a release contains

Each file exists for `x64` and `arm64`:

| File | What it is |
|---|---|
| `TTSMixmaster-v<ver>-setup-<arch>.exe` | Per-user installer (WiX Burn bundle). No admin rights; installs to `%LOCALAPPDATA%\Programs\TTSMixmaster`. |
| `TTSMixmaster-v<ver>-<arch>.msi` | Per-machine MSI. Needs admin; installs to `C:\Program Files\TTSMixmaster`. Upgrades the old 1.x (Python) MSI. |
| `TTSMixmaster-v<ver>-<arch>.msix` | MSIX of the same app. |
| `TTSMixmaster-v<ver>-portable-<arch>.zip` | The self-contained publish folder. Unzip and run `TTSMixmaster.exe`. |

All four contain the same files: the unpackaged, self-contained (.NET and Windows App SDK) publish
folder from `src/TTSMixmaster.App`, plus the pinned, hash-checked `yt-dlp.exe`, `ffmpeg.exe` and
`ffprobe.exe` from [`scripts/fetch-tools.ps1`](../scripts/fetch-tools.ps1). Those tools are x64
builds; on ARM64 Windows they run under emulation.

Both installers create Start Menu and Desktop shortcuts and upgrade in place (stable
`UpgradeCode` + `MajorUpgrade`). They use different UpgradeCodes, so a per-user and a per-machine
install don't replace each other.

### How it's built

[`scripts/package-release.ps1`](../scripts/package-release.ps1) does the work in stages, so CI can
sign between them. Locally, `-Stage All` builds everything unsigned:

```powershell
./scripts/package-release.ps1 -Arch x64
# -> artifacts/release/
```

| Stage | Does | CI then signs |
|---|---|---|
| `Publish` | `dotnet publish` the App, add yt-dlp/ffmpeg | `TTSMixmaster.exe`, `TTSMixmaster.dll`, `TTSMixmaster.Core.dll` |
| `Package` | portable zip, per-machine MSI, per-user MSI, MSIX | both MSIs and the MSIX |
| `Bundle` | build `setup.exe` around the **signed** per-user MSI, detach its Burn engine | the Burn engine |
| `Reattach` | reattach the signed engine | `setup.exe` |

Third-party binaries (yt-dlp, FFmpeg, the .NET and Windows App SDK runtime files) are never
re-signed. The last CI step checks every signature with `signtool verify /pa`.

Installer sources live in [`installer/`](../installer):

- `TTSMixmaster.wxs`: one MSI source, built twice. `TTSMixmaster.wixproj` builds it per-machine,
  `peruser/TTSMixmaster.PerUser.wixproj` per-user. The WiX 5 `<Files>` element harvests the
  publish folder at build time (no Heat).
- `bundle/`: the Burn bundle behind `setup.exe`.
- `msix/`: `Package.appxmanifest`, its icons and `priconfig.xml`. The script stamps `Version`
  (`X.Y.Z.0`) and `ProcessorArchitecture` into a copy, builds a package layout from the publish
  folder, and runs `winapp package` on it. The app is built unpackaged, so its XAML resources
  live in `TTSMixmaster.pri`; a packaged process reads `resources.pri` instead. The script
  therefore merges `TTSMixmaster.pri` and the icons into the package's `resources.pri` with
  makepri (`priconfig.xml`). Without that, the MSIX installs but crashes on launch. The App
  stays self-contained (.NET and Windows App SDK) inside the MSIX, so the package has no
  framework dependencies. The manifest's `Publisher` is committed and must equal the Trusted Signing certificate
  subject (see below).
- `.config/dotnet-tools.json` pins the `wix` CLI used for the Burn engine detach/reattach.

**Why WiX 5.0.2:** it's the drop-in successor to the WiX 4 the old MSI used, adds `<Files>`
harvesting, and predates the Open Source Maintenance Fee EULA that WiX 6+ binaries carry.
**Why setup.exe is Burn around a per-user MSI:** a `Scope="perUser"` MSI never asks for admin
rights and keeps normal MSI upgrade/uninstall behaviour; Burn turns it into a single signed `.exe`
with one Apps & Features entry. The other approach, a dual-purpose `ALLUSERS=2` MSI, depends on
`MSIINSTALLPERUSER` folder redirection and on how Burn classifies such packages, which makes it
harder to predict.

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

These are the same secrets the old PyInstaller pipeline used; nothing new is required.

The app registration needs the **Trusted Signing Certificate Profile Signer** role on the
certificate profile. The federated credential is bound to `main`, so a manual run from another
branch fails at Azure login unless you add a credential for that branch.

### MSIX publisher

`Identity/@Publisher` in `installer/msix/Package.appxmanifest` is
`CN=Morningwood Gaming LLC, O=Morningwood Gaming LLC, L=Farmington Hills, S=Michigan, C=US`, the
subject of the Trusted Signing certificate. If it doesn't match the certificate exactly (same
order, same spacing), signing the MSIX fails. Trusted Signing renews the certificate often but
keeps the subject; if the subject ever changes, update the manifest. To check it against a
signed file:

```powershell
(Get-AuthenticodeSignature .\TTSMixmaster.exe).SignerCertificate.Subject
```

## winget (optional)

[`.github/workflows/winget.yml`](../.github/workflows/winget.yml) runs when a release is
published and uses [komac](https://github.com/russellbanks/Komac) (pinned version, hash-checked)
to open a PR against `microsoft/winget-pkgs` that adds the new version. Until you opt in it skips.

It submits the two **MSIs** (x64 and arm64). MSIs carry a ProductCode and UpgradeCode, so winget
detects installs, upgrades and uninstalls exactly, and a package manager is expected to do the
per-machine install anyway.

`komac update` only adds versions to a package that already exists, so the first submission is
manual:

1. Publish a release with this pipeline.
2. On your PC, install komac (`winget install RussellBanks.Komac`) and add a **classic** GitHub
   token with the `public_repo` scope: `komac token add`. Fine-grained tokens can't open the
   winget-pkgs PR.
3. Create the package:
   ```powershell
   komac new MorningwoodGaming.TTSMixmaster --version 2.0.0 `
     --urls https://github.com/MWG-Logan/TTSMixmaster/releases/download/v2.0.0/TTSMixmaster-v2.0.0-x64.msi `
            https://github.com/MWG-Logan/TTSMixmaster/releases/download/v2.0.0/TTSMixmaster-v2.0.0-arm64.msi
   ```
   Answer the metadata prompts (publisher, MIT license, short description, homepage) and let it
   submit. komac forks `winget-pkgs` under your account if needed.
4. Wait for the winget moderators to merge the PR.
5. Opt in:
   - secret `WINGET_TOKEN`: a classic PAT with `public_repo` scope, from the account that owns the
     `winget-pkgs` fork;
   - variable `WINGET_ENABLED` = `true`;
   - variable `WINGET_PACKAGE_ID`, only if you used a different identifier than
     `MorningwoodGaming.TTSMixmaster`.

From then on every published release opens a winget PR automatically. To submit an existing
release, run **Publish to winget** manually with its tag (for example `v2.0.0`).

## Testing artifacts locally

The owner's machine has a Defender ASR rule that blocks new unsigned executables outside excluded
folders. Copy unsigned local builds into `artifacts-local/` in the main checkout (excluded and
git-ignored) before running them, and test installs in Windows Sandbox. To install the unsigned
MSIX, sign it with a throwaway certificate that has the same subject as the manifest:

```powershell
winapp cert generate --output devcert.pfx --valid-days 7 `
  --publisher 'CN=Morningwood Gaming LLC, O=Morningwood Gaming LLC, L=Farmington Hills, S=Michigan, C=US'
winapp sign artifacts/release/TTSMixmaster-v2.0.0-x64.msix devcert.pfx --password <its password>
# trust devcert.pfx on the test machine (winapp cert install, admin), then Add-AppxPackage
```
