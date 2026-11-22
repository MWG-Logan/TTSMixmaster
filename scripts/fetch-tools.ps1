# Fetches the pinned yt-dlp and FFmpeg (ffmpeg + ffprobe) builds for one architecture into
# OutputFolder. Every download is checked against its independently published SHA-256; update
# a URL and its hash together.
#
#   x64:   yt-dlp.exe 2026.10.26, FFmpeg 8.0.1 "essentials" (gyan.dev build, GPL v3+)
#          https://github.com/GyanD/codexffmpeg/releases/tag/8.0.1
#   arm64: yt-dlp_arm64.exe 2026.10.26 (installed as yt-dlp.exe), FFmpeg n8.1.3 winarm64 GPL
#          (BtbN build). BtbN keeps each month's last autobuild for two years, so pin a
#          month-end tag: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-31-12-59
#
# FFmpeg in both builds is GPL; ship its license (ffmpeg-LICENSE.txt) and point to the
# corresponding source when distributing. yt-dlp source is Unlicense; its standalone builds
# bundle separately licensed components.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputFolder,
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$pins = @{
    x64   = @{
        YtDlpUrl    = 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe'
        YtDlpSha256 = 'f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d'
        FfmpegUrl    = 'https://github.com/GyanD/codexffmpeg/releases/download/8.0.1/ffmpeg-8.0.1-essentials_build.zip'
        FfmpegSha256 = 'e2aaeaa0fdbc397d4794828086424d4aaa2102cef1fb6874f6ffd29c0b88b673'
        LicenseName  = 'LICENSE'
        Summary      = 'yt-dlp 2026.10.26 and FFmpeg 8.0.1 (x64)'
    }
    arm64 = @{
        YtDlpUrl    = 'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp_arm64.exe'
        YtDlpSha256 = '0066dbac3c124028e2dcae2a197fccee33cd91f456313d24f7af89fd54590e98'
        FfmpegUrl    = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/ffmpeg-n8.1.3-27-g8dca1586f4-winarm64-gpl-8.1.zip'
        FfmpegSha256 = '40029da82e02abf0d12491e23666e710b7a8943d13501594cf38e6135b95efba'
        LicenseName  = 'LICENSE.txt'
        Summary      = 'yt-dlp 2026.10.26 and FFmpeg n8.1.3 (ARM64)'
    }
}
$pin = $pins[$Arch]

$outputRoot = [IO.Path]::GetFullPath($OutputFolder)
$stage = Join-Path $outputRoot ('.fetch-tools-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null

function Get-VerifiedDownload([string]$Url, [string]$File, [string]$Sha256) {
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $File -TimeoutSec 300
    $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) { throw "SHA-256 mismatch for $Url (expected $Sha256, got $actual)." }
}

try {
    # Always installed as yt-dlp.exe, whatever the release asset is called.
    Get-VerifiedDownload $pin.YtDlpUrl (Join-Path $stage 'yt-dlp.exe') $pin.YtDlpSha256
    $archive = Join-Path $stage 'ffmpeg.zip'
    Get-VerifiedDownload $pin.FfmpegUrl $archive $pin.FfmpegSha256

    # Extract only named payloads, never arbitrary archive paths. ffprobe is used by yt-dlp.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', $pin.LicenseName)) {
            $entries = @($zip.Entries | Where-Object { $_.Name -ceq $name })
            if ($entries.Count -ne 1) { throw "Expected exactly one $name in the pinned FFmpeg archive." }
            $target = if ($name -eq $pin.LicenseName) { 'ffmpeg-LICENSE.txt' } else { $name }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], (Join-Path $stage $target))
        }
    }
    finally { $zip.Dispose() }

    # Publish only after BOTH downloads pass verification and the archive is valid.
    foreach ($name in @('yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe', 'ffmpeg-LICENSE.txt')) {
        Move-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $outputRoot $name) -Force
    }
    Write-Output "Verified $($pin.Summary) (including ffprobe) in $outputRoot"
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $expectedParent = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside output folder: $resolvedStage"
    }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
