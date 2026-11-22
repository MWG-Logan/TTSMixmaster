# Guest side of tests/e2e/ui-smoke.ps1 -DevSource (runs inside Windows Sandbox via `winapp target exec`).
# Sets up everything a Build + Export to TTS needs, on loopback only:
#   - Azurite (blob) from the pushed node.exe + azurite.zip, with a public-read "tts-audio" container;
#   - a tiny HTTP server for the dev collection's audio (fixtures.js serve);
#   - %LOCALAPPDATA%\TTSMixmaster\config.json and dev-collection.json for the dev-source build.
# The Sandbox is shared: an existing TTSMixmaster data folder is moved aside and teardown.ps1 puts it back.
[CmdletBinding()]
param(
    [int]$BlobPort = 10100,
    [int]$MediaPort = 10180
)
$ErrorActionPreference = 'Stop'
$work = $PSScriptRoot
$state = Join-Path $work 'state.json'
$data = Join-Path $env:LOCALAPPDATA 'TTSMixmaster'
$root = Join-Path $work 'run'
$node = Join-Path $work 'node.exe'

if (Test-Path $state) { throw "A previous e2e setup was not torn down ($state). Run teardown.ps1 first." }
$backup = $null
if (Test-Path $data) {
    $backup = "$data.e2e-backup-$(Get-Date -Format yyyyMMddHHmmss)"
    Move-Item $data $backup
}
$record = [ordered]@{ backup = $backup; pids = @() }
$record | ConvertTo-Json | Set-Content $state -Encoding utf8

New-Item -ItemType Directory -Force $data, $root, "$root\media", "$root\music", "$root\Saved Objects" | Out-Null
Copy-Item (Join-Path $work 'tone.mp3') "$root\media\tone.mp3"

if (-not (Test-Path "$work\azurite\dist\src\blob\main.js")) {
    # tar.exe (inbox since Windows 10 1803) unpacks the ~14k files in seconds; Expand-Archive takes minutes.
    New-Item -ItemType Directory -Force (Join-Path $work 'azurite') | Out-Null
    tar.exe -xf (Join-Path $work 'azurite.zip') -C (Join-Path $work 'azurite')
    if ($LASTEXITCODE -ne 0) { throw 'Could not unpack azurite.zip.' }
}

# Through WMI, not Start-Process: `winapp target exec` returns only when every handle to its output pipe is closed,
# and Start-Process (with redirection) lets the child inherit those handles, so the exec would never return.
# Win32_Process.Create shares no handles with this process; the window is hidden; output goes to a log file.
function Start-Hidden([string[]]$Arguments) {
    $log = "$root\$($record.pids.Count).log.txt"
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
    $created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "cmd.exe /d /c `"`"$node`" $($Arguments -join ' ') > `"$log`" 2>&1`""
        CurrentDirectory = $work
        ProcessStartupInformation = $startup
    }
    if ($created.ReturnValue -ne 0) { throw "Couldn't start node $($Arguments -join ' ') (Win32_Process.Create $($created.ReturnValue))." }
    $record.pids += [int]$created.ProcessId
    $record | ConvertTo-Json | Set-Content $state -Encoding utf8
}
function Wait-Port([int]$Port) {
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $client = [Net.Sockets.TcpClient]::new()
        try { $client.Connect('127.0.0.1', $Port); return } catch { Start-Sleep -Milliseconds 250 } finally { $client.Dispose() }
    }
    $logs = Get-ChildItem "$root\*.log.txt" | ForEach-Object { "--- $($_.Name)`n$(Get-Content $_ -Raw)" }
    throw "Nothing is listening on 127.0.0.1:$Port.`n$($logs -join "`n")"
}

Start-Hidden @("`"$work\azurite\dist\src\blob\main.js`"", '--blobHost', '127.0.0.1', '--blobPort', "$BlobPort",
    '--inMemoryPersistence', '--skipApiVersionCheck')
Start-Hidden @("`"$work\fixtures.js`"", 'serve', "`"$root\media`"", "$MediaPort")
Wait-Port $BlobPort
Wait-Port $MediaPort

$endpoint = "http://127.0.0.1:$BlobPort/devstoreaccount1"
& $node "$work\fixtures.js" container $endpoint 'tts-audio'
if ($LASTEXITCODE -ne 0) { throw 'Could not create the Azurite container.' }

# Azurite's published development key (not a secret).
$connection = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=$endpoint;"
[ordered]@{
    download_path = "$root\music"
    tts_output_path = "$root\music\_tts"
    tts_saved_objects_path = "$root\Saved Objects"
    azure_storage_connection_string = $connection
    azure_container_name = 'tts-audio'
} | ConvertTo-Json | Set-Content (Join-Path $data 'config.json') -Encoding utf8

$track = "http://127.0.0.1:$MediaPort/tone.mp3"
[ordered]@{
    id = 'e2e-collection'; name = 'E2E Tavern'; description = 'Local test collection for the e2e smoke run'; thumbnailUrl = ''
    tracks = @(
        [ordered]@{ title = 'Opening Tone'; artist = 'E2E'; album = 'Smoke'; durationSeconds = 3; sourceUrl = "${track}?1" },
        [ordered]@{ title = 'Closing Tone'; artist = 'E2E'; album = 'Smoke'; durationSeconds = 3; sourceUrl = "${track}?2" }
    )
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $data 'dev-collection.json') -Encoding utf8

Write-Output "ready: blob $endpoint, media $track, saved objects $root\Saved Objects"
