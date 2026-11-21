# Windows x64 tools. Update the version AND its independently published release SHA-256 together.
# FFmpeg essentials is GPL v3 or later (not the LGPL-only build); retain its license
# and provide corresponding source when distributing: https://github.com/GyanD/codexffmpeg/releases/tag/8.0.1
# yt-dlp source is Unlicense; its standalone distribution bundles separately licensed components.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputFolder)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$outputRoot = [IO.Path]::GetFullPath($OutputFolder)
$stage = Join-Path $outputRoot ('.fetch-tools-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null

function Get-VerifiedDownload([string]$Url, [string]$File, [string]$Sha256) {
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $File -TimeoutSec 300
    $actual = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) { throw "SHA-256 mismatch for $Url (expected $Sha256, got $actual)." }
}

try {
    Get-VerifiedDownload `
        'https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.26/yt-dlp.exe' `
        (Join-Path $stage 'yt-dlp.exe') `
        'f5389d3698ecde4ef06f4e6b1ce0294fe9ac1396228c8990b8d3416d6d177a7d'
    $archive = Join-Path $stage 'ffmpeg.zip'
    Get-VerifiedDownload `
        'https://github.com/GyanD/codexffmpeg/releases/download/8.0.1/ffmpeg-8.0.1-essentials_build.zip' `
        $archive `
        'e2aaeaa0fdbc397d4794828086424d4aaa2102cef1fb6874f6ffd29c0b88b673'

    # Extract only named payloads, never arbitrary archive paths. ffprobe is used by yt-dlp.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', 'LICENSE')) {
            $entries = @($zip.Entries | Where-Object { $_.Name -ceq $name })
            if ($entries.Count -ne 1) { throw "Expected exactly one $name in the pinned FFmpeg archive." }
            $target = if ($name -eq 'LICENSE') { 'ffmpeg-LICENSE.txt' } else { $name }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], (Join-Path $stage $target))
        }
    }
    finally { $zip.Dispose() }

    # Publish only after BOTH downloads pass verification and the archive is valid.
    foreach ($name in @('yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe', 'ffmpeg-LICENSE.txt')) {
        Move-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $outputRoot $name) -Force
    }
    Write-Output "Verified yt-dlp 2026.10.26 and FFmpeg 8.0.1 (including ffprobe) in $outputRoot"
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $expectedParent = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing cleanup outside output folder: $resolvedStage"
    }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
