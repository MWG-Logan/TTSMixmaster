<#
.SYNOPSIS
  Builds the TTSMixmaster release artifacts for one architecture.

.DESCRIPTION
  Produces, in artifacts/release/:
    TTSMixmaster-v<ver>-portable-<arch>.zip   self-contained publish folder + yt-dlp/ffmpeg
    TTSMixmaster-v<ver>-<arch>.msi            per-machine MSI (Program Files)
    TTSMixmaster-v<ver>-setup-<arch>.exe      per-user Burn bundle (%LOCALAPPDATA%\Programs, no admin)
    TTSMixmaster-v<ver>-<arch>.msix           MSIX of the same publish folder

  The version comes from <Version> in Directory.Build.props.

  CI runs the stages one at a time so it can sign in between (see
  .github/workflows/build-and-release.yml):

    Publish   dotnet publish + fetch-tools          -> sign TTSMixmaster.exe/.dll, TTSMixmaster.Core.dll
    Package   zip, MSI, per-user MSI, MSIX          -> sign both MSIs and the MSIX
    Bundle    build setup.exe around the signed per-user MSI, detach its Burn engine
                                                    -> sign the engine
    Reattach  reattach the signed engine            -> sign the final setup.exe

  -Stage All runs everything unsigned for local testing (no engine detach).

.EXAMPLE
  ./scripts/package-release.ps1 -Arch x64
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',

    [ValidateSet('All', 'Publish', 'Package', 'Bundle', 'Reattach')]
    [string]$Stage = 'All',

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

$publishDir = Join-Path $ArtifactsDir "publish\$Arch"
$workDir = Join-Path $ArtifactsDir "work\$Arch"
$releaseDir = Join-Path $ArtifactsDir 'release'
$installerDir = Join-Path $repoRoot 'installer'

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
        throw "<Version> in Directory.Build.props must be MAJOR.MINOR.PATCH (MSI/MSIX can't carry a pre-release suffix). Got '$value'."
    }
    return $value
}

function Assert-Published {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'TTSMixmaster.exe'))) {
        throw "No publish output at $publishDir. Run -Stage Publish first."
    }
    foreach ($tool in 'yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $tool))) { throw "$tool is missing from $publishDir." }
    }
}

# Builds a .wixproj into its own per-arch bin/obj under artifacts/work, returns the bin folder.
function Invoke-WixBuild([string]$Project, [string]$Name, [string[]]$Extra = @()) {
    $out = Join-Path $workDir "$Name\bin"
    $obj = Join-Path $workDir "$Name\obj"
    $buildArgs = @('build', $Project, '-c', 'Release', '-nologo', '-v:minimal',
        "-p:InstallerPlatform=$Arch", "-p:AppPublishDir=$publishDir",
        # MSBuild wants the trailing separator; '/' (not '\') so it can't escape a closing quote.
        "-p:OutputPath=$out/", "-p:IntermediateOutputPath=$obj/") + $Extra
    Invoke-Native 'dotnet' $buildArgs
    return $out
}

function Invoke-Wix([string[]]$WixArgs) {
    Push-Location $installerDir
    try {
        Invoke-Native 'dotnet' @('tool', 'restore')
        Invoke-Native 'dotnet' (@('tool', 'run', 'wix', '--') + $WixArgs)
    }
    finally { Pop-Location }
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

function New-Msix([string]$Version, [string]$Destination) {
    $msixSource = Join-Path $installerDir 'msix'
    $msixWork = Join-Path $workDir 'msix'
    $layout = Join-Path $msixWork 'layout'
    $priInput = Join-Path $msixWork 'pri-input'
    $manifestPath = Join-Path $msixWork 'Package.appxmanifest'

    # The package layout is a copy of the publish folder plus the tile icons, so the
    # zip/MSI payload (the publish folder itself) stays untouched.
    New-Item -ItemType Directory -Path $msixWork, $priInput -Force | Out-Null
    Copy-Item -LiteralPath $publishDir -Destination $layout -Recurse
    Copy-Item -LiteralPath (Join-Path $msixSource 'Assets') -Destination $msixWork -Recurse
    Copy-Item -Path (Join-Path $msixSource 'Assets\*') -Destination (Join-Path $layout 'Assets') -Force
    Copy-Item -LiteralPath (Join-Path $msixSource 'Package.appxmanifest') -Destination $manifestPath

    $manifest = New-Object xml
    $manifest.PreserveWhitespace = $true
    $manifest.Load($manifestPath)
    $identity = $manifest.Package.Identity
    $identity.SetAttribute('Version', "$Version.0")
    if ($MsixPublisher) { $identity.SetAttribute('Publisher', $MsixPublisher.Trim()) }
    $identity.SetAttribute('ProcessorArchitecture', $Arch)
    $manifest.Save($manifestPath)

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

$version = Get-ReleaseVersion
$base = "TTSMixmaster-v$version"
$names = @{
    Zip   = "$base-portable-$Arch.zip"
    Msi   = "$base-$Arch.msi"
    Setup = "$base-setup-$Arch.exe"
    Msix  = "$base-$Arch.msix"
}
$perUserMsi = Join-Path $workDir 'TTSMixmaster.PerUser.msi'
$bundleExe = Join-Path $workDir 'TTSMixmaster.Setup.exe'
$burnEngine = Join-Path $workDir 'burn-engine.exe'
Write-Host "TTSMixmaster $version ($Arch), stage $Stage"

if ($Stage -in 'All', 'Publish') {
    if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
    $platform = @{ x64 = 'x64'; arm64 = 'ARM64' }[$Arch]
    Invoke-Native 'dotnet' @('publish', (Join-Path $repoRoot 'src\TTSMixmaster.App\TTSMixmaster.App.csproj'),
        '-c', 'Release', '-nologo', "-p:Platform=$platform", "-p:PublishDir=$publishDir",
        # Stopgap: without MSIX tooling the publish output lacks TTSMixmaster.pri and the app
        # crashes at startup. Redundant once the App csproj sets EnableMsixTooling itself.
        '-p:EnableMsixTooling=true')
    # The pinned tools are x64 builds; on ARM64 Windows they run under x64 emulation.
    & (Join-Path $PSScriptRoot 'fetch-tools.ps1') -OutputFolder $publishDir
    Assert-Published
}

if ($Stage -in 'All', 'Package') {
    Assert-Published
    if (Test-Path -LiteralPath $workDir) { Remove-Item -LiteralPath $workDir -Recurse -Force }
    New-Item -ItemType Directory -Path $workDir, $releaseDir -Force | Out-Null
    foreach ($name in $names.Values) {
        $existing = Join-Path $releaseDir $name
        if (Test-Path -LiteralPath $existing) { Remove-Item -LiteralPath $existing -Force }
    }

    Write-Host "Portable zip"
    New-PortableZip $publishDir (Join-Path $releaseDir $names.Zip)

    Write-Host "Per-machine MSI"
    $bin = Invoke-WixBuild (Join-Path $installerDir 'TTSMixmaster.wixproj') 'msi'
    Copy-Item (Join-Path $bin 'TTSMixmaster.msi') (Join-Path $releaseDir $names.Msi)

    Write-Host "Per-user MSI (for setup.exe)"
    $bin = Invoke-WixBuild (Join-Path $installerDir 'peruser\TTSMixmaster.PerUser.wixproj') 'peruser'
    Copy-Item (Join-Path $bin 'TTSMixmaster.PerUser.msi') $perUserMsi

    Write-Host "MSIX"
    New-Msix $version (Join-Path $releaseDir $names.Msix)
}

if ($Stage -in 'All', 'Bundle') {
    if (-not (Test-Path -LiteralPath $perUserMsi)) { throw "Missing $perUserMsi. Run -Stage Package first." }
    Write-Host "setup.exe bundle"
    $bin = Invoke-WixBuild (Join-Path $installerDir 'bundle\TTSMixmaster.Bundle.wixproj') 'bundle' @("-p:PerUserMsi=$perUserMsi")
    Copy-Item (Join-Path $bin 'TTSMixmaster.Setup.exe') $bundleExe -Force

    if ($Stage -eq 'Bundle') {
        # Burn bundles must be signed in two passes: the engine (setup.exe's own code, which
        # Burn extracts and runs from a temp folder) and then the whole bundle. Detach the
        # engine here; CI signs it, then -Stage Reattach puts it back.
        if (Test-Path -LiteralPath $burnEngine) { Remove-Item -LiteralPath $burnEngine -Force }
        Invoke-Wix @('burn', 'detach', $bundleExe, '-engine', $burnEngine)
    }
    else {
        Copy-Item $bundleExe (Join-Path $releaseDir $names.Setup) -Force
    }
}

if ($Stage -eq 'Reattach') {
    if (-not (Test-Path -LiteralPath $burnEngine)) { throw "Missing $burnEngine. Run -Stage Bundle first." }
    $setup = Join-Path $releaseDir $names.Setup
    Invoke-Wix @('burn', 'reattach', $bundleExe, '-engine', $burnEngine, '-o', $setup)
}

if ($Stage -in 'All', 'Package', 'Reattach') {
    Get-ChildItem -LiteralPath $releaseDir -Filter "$base-*$Arch*" |
        ForEach-Object { '{0,-48} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) } | Write-Host
}
