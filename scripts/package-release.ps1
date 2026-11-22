<#
.SYNOPSIS
  Builds the TTSMixmaster release artifacts.

.DESCRIPTION
  Produces, in artifacts/release/:
    TTSMixmaster-v<ver>.msixbundle            x64 + ARM64 MSIX bundle (the main download)
    TTSMixmaster.appinstaller                 App Installer file that installs the bundle and
                                              keeps it updated from the latest GitHub release
    TTSMixmaster-v<ver>-portable-<arch>.zip   self-contained publish folder + yt-dlp/ffmpeg

  The version comes from <Version> in Directory.Build.props.

  Stages (CI runs them separately so it can sign in between; see
  .github/workflows/build-and-release.yml):

    Publish   per arch: dotnet publish + fetch-tools      -> CI signs TTSMixmaster.exe/.dll and
                                                             TTSMixmaster.Core.dll
    Package   per arch: portable zip, unsigned .msix      (the .msix goes to artifacts/msix/)
    Bundle    .msixbundle from artifacts/msix/*.msix,     -> CI signs the .msixbundle
              plus TTSMixmaster.appinstaller

  -Stage All runs every stage for every -Arch, unsigned, for local testing.

.EXAMPLE
  ./scripts/package-release.ps1                      # everything, x64 + arm64
  ./scripts/package-release.ps1 -Arch x64 -Stage Publish
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string[]]$Arch = @('x64', 'arm64'),

    [ValidateSet('All', 'Publish', 'Package', 'Bundle')]
    [string]$Stage = 'All',

    # Base URL of the GitHub releases the .appinstaller points at.
    [string]$ReleasesUrl = 'https://github.com/MWG-Logan/TTSMixmaster/releases',

    # Optional override of the MSIX Identity/@Publisher committed in
    # installer/msix/Package.appxmanifest (which equals the Trusted Signing certificate
    # subject). Only for local testing with a certificate that has a different subject.
    [string]$MsixPublisher,

    [string]$ArtifactsDir
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path $repoRoot 'artifacts' }
$ArtifactsDir = [IO.Path]::GetFullPath($ArtifactsDir)
$releaseDir = Join-Path $ArtifactsDir 'release'
$msixDir = Join-Path $ArtifactsDir 'msix'
$msixSource = Join-Path $repoRoot 'installer\msix'

function Invoke-Native([string]$Command, [string[]]$Arguments) {
    Write-Host "> $Command $($Arguments -join ' ')"
    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "'$Command' failed with exit code $LASTEXITCODE." }
}

function Get-ReleaseVersion {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    $value = (& dotnet msbuild $props -nologo -getProperty:Version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Could not read <Version> from $props." }
    if ($value -notmatch '^\d+\.\d+\.\d+$') {
        throw "<Version> in Directory.Build.props must be MAJOR.MINOR.PATCH (MSIX can't carry a pre-release suffix). Got '$value'."
    }
    return $value
}

function Get-PublishDir([string]$A) { Join-Path $ArtifactsDir "publish\$A" }
function Get-WorkDir([string]$A) { Join-Path $ArtifactsDir "work\$A" }

function Assert-Published([string]$A) {
    $dir = Get-PublishDir $A
    foreach ($file in 'TTSMixmaster.exe', 'TTSMixmaster.pri', 'yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $dir $file))) { throw "$file is missing from $dir. Run -Stage Publish first." }
    }
}

# The manifest with this build's Version and (optionally) a publisher override stamped in.
function Get-StampedManifest([string]$Version, [string]$A) {
    $manifest = New-Object xml
    $manifest.PreserveWhitespace = $true
    $manifest.Load((Join-Path $msixSource 'Package.appxmanifest'))
    $identity = $manifest.Package.Identity
    $identity.SetAttribute('Version', "$Version.0")
    if ($MsixPublisher) { $identity.SetAttribute('Publisher', $MsixPublisher.Trim()) }
    if ($A) { $identity.SetAttribute('ProcessorArchitecture', $A) }
    return $manifest
}

function New-PortableZip([string]$Source, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $root = [IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
    $zip = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        # One top-level TTSMixmaster\ folder so "extract here" doesn't spill ~500 files.
        foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
            $entry = 'TTSMixmaster/' + $file.FullName.Substring($root.Length).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $zip.Dispose() }
}

function New-Msix([string]$Version, [string]$A, [string]$Destination) {
    $publishDir = Get-PublishDir $A
    $msixWork = Join-Path (Get-WorkDir $A) 'msix'
    $layout = Join-Path $msixWork 'layout'
    $priInput = Join-Path $msixWork 'pri-input'
    $manifestPath = Join-Path $msixWork 'Package.appxmanifest'

    # The package layout is a copy of the publish folder plus the tile icons, so the
    # portable zip's payload (the publish folder itself) stays untouched.
    New-Item -ItemType Directory -Path $msixWork, $priInput -Force | Out-Null
    Copy-Item -LiteralPath $publishDir -Destination $layout -Recurse
    Copy-Item -LiteralPath (Join-Path $msixSource 'Assets') -Destination $msixWork -Recurse
    Copy-Item -Path (Join-Path $msixSource 'Assets\*') -Destination (Join-Path $layout 'Assets') -Force
    (Get-StampedManifest $Version $A).Save($manifestPath)

    # A packaged process loads XAML and WinUI resources from resources.pri, whose primary
    # resource map must be the package's. The unpackaged build keeps them in TTSMixmaster.pri
    # under a map named "TTSMixmaster". Merge that PRI and the tile icons into one
    # resources.pri for the package (installer/msix/priconfig.xml), instead of the
    # icons-only resources.pri winapp would generate.
    Copy-Item -LiteralPath (Join-Path $publishDir 'TTSMixmaster.pri') -Destination $priInput
    Copy-Item -LiteralPath (Join-Path $msixSource 'Assets') -Destination $priInput -Recurse
    Invoke-Native 'winapp' @('tool', 'makepri', 'new', '/pr', $priInput, '/cf', (Join-Path $msixSource 'priconfig.xml'),
        '/mn', $manifestPath, '/of', (Join-Path $layout 'resources.pri'), '/o')

    Invoke-Native 'winapp' @('package', $layout, '--manifest', $manifestPath, '--output', $Destination,
        '--skip-pri', '--no-sign', '--quiet')
}

function New-AppInstaller([string]$Version, [string]$Destination) {
    $identity = (Get-StampedManifest $Version $null).Package.Identity
    $base = $ReleasesUrl.TrimEnd('/')
    $ns = 'http://schemas.microsoft.com/appx/appinstaller/2018'
    $xml = New-Object xml
    $xml.AppendChild($xml.CreateXmlDeclaration('1.0', 'utf-8', $null)) | Out-Null
    $root = $xml.AppendChild($xml.CreateElement('AppInstaller', $ns))
    $root.SetAttribute('Version', "$Version.0")
    # Stable URL that always serves the newest .appinstaller: this is what installed copies
    # poll for updates. GitHub redirects it to the asset of the latest (non-prerelease) release.
    $root.SetAttribute('Uri', "$base/latest/download/TTSMixmaster.appinstaller")
    $bundle = $root.AppendChild($xml.CreateElement('MainBundle', $ns))
    $bundle.SetAttribute('Name', $identity.Name)
    $bundle.SetAttribute('Publisher', $identity.Publisher)
    $bundle.SetAttribute('Version', "$Version.0")
    $bundle.SetAttribute('Uri', "$base/download/v$Version/TTSMixmaster-v$Version.msixbundle")
    $update = $root.AppendChild($xml.CreateElement('UpdateSettings', $ns))
    $onLaunch = $update.AppendChild($xml.CreateElement('OnLaunch', $ns))
    $onLaunch.SetAttribute('HoursBetweenUpdateChecks', '24')
    $onLaunch.SetAttribute('ShowPrompt', 'true')
    $onLaunch.SetAttribute('UpdateBlocksActivation', 'false')
    $update.AppendChild($xml.CreateElement('AutomaticBackgroundTask', $ns)) | Out-Null

    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $writer = [System.Xml.XmlWriter]::Create($Destination, $settings)
    try { $xml.Save($writer) } finally { $writer.Dispose() }
}

$version = Get-ReleaseVersion
$base = "TTSMixmaster-v$version"
$bundleName = "$base.msixbundle"
Write-Host "TTSMixmaster $version ($($Arch -join ', ')), stage $Stage"
New-Item -ItemType Directory -Path $releaseDir, $msixDir -Force | Out-Null

foreach ($a in $Arch) {
    $publishDir = Get-PublishDir $a

    if ($Stage -in 'All', 'Publish') {
        if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
        $platform = @{ x64 = 'x64'; arm64 = 'ARM64' }[$a]
        Invoke-Native 'dotnet' @('publish', (Join-Path $repoRoot 'src\TTSMixmaster.App\TTSMixmaster.App.csproj'),
            '-c', 'Release', '-nologo', "-p:Platform=$platform", "-p:PublishDir=$publishDir")
        & (Join-Path $PSScriptRoot 'fetch-tools.ps1') -OutputFolder $publishDir -Arch $a
        Assert-Published $a
    }

    if ($Stage -in 'All', 'Package') {
        Assert-Published $a
        $workDir = Get-WorkDir $a
        if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force }

        Write-Host "Portable zip ($a)"
        New-PortableZip $publishDir (Join-Path $releaseDir "$base-portable-$a.zip")

        Write-Host "MSIX ($a)"
        $msix = Join-Path $msixDir "$base-$a.msix"
        if (Test-Path -LiteralPath $msix) { Remove-Item -LiteralPath $msix -Force }
        New-Msix $version $a $msix
    }
}

if ($Stage -in 'All', 'Bundle') {
    # Bundle exactly this version's per-arch packages (CI downloads them into artifacts/msix).
    $bundleInput = Join-Path $ArtifactsDir 'work\bundle-input'
    if (Test-Path -LiteralPath $bundleInput) { Remove-Item -LiteralPath $bundleInput -Recurse -Force }
    New-Item -ItemType Directory -Path $bundleInput -Force | Out-Null
    foreach ($a in $Arch) {
        $msix = Join-Path $msixDir "$base-$a.msix"
        if (-not (Test-Path -LiteralPath $msix)) { throw "Missing $msix. Run -Stage Package for $a first." }
        Copy-Item -LiteralPath $msix -Destination $bundleInput
    }

    Write-Host "MSIX bundle"
    $bundle = Join-Path $releaseDir $bundleName
    Invoke-Native 'winapp' @('tool', 'makeappx', 'bundle', '/d', $bundleInput, '/p', $bundle, '/bv', "$version.0", '/o')

    Write-Host "App Installer file"
    New-AppInstaller $version (Join-Path $releaseDir 'TTSMixmaster.appinstaller')
}

if ($Stage -in 'All', 'Package', 'Bundle') {
    Get-ChildItem -LiteralPath $releaseDir -File | Where-Object { $_.Name -like "$base*" -or $_.Name -eq 'TTSMixmaster.appinstaller' } |
        ForEach-Object { '{0,-48} {1,10:N1} MB' -f $_.Name, ($_.Length / 1MB) } | Write-Host
}
