# Undoes setup.ps1 inside the Sandbox: stops the fixture processes it started (only those PIDs), removes the
# e2e TTSMixmaster data folder and puts back the one that was there before. Never touches the Sandbox itself.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$work = $PSScriptRoot
$state = Join-Path $work 'state.json'
$data = Join-Path $env:LOCALAPPDATA 'TTSMixmaster'
if (-not (Test-Path $state)) { Write-Output 'nothing to tear down'; return }
$record = Get-Content $state -Raw | ConvertFrom-Json
foreach ($id in @($record.pids)) {
    # Each recorded PID is the cmd.exe that setup.ps1 started; its tree is the node server.
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -in 'cmd', 'node') { & taskkill.exe /PID $id /T /F | Out-Null }
}
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
if ($record.backup -and (Test-Path $record.backup)) { Move-Item $record.backup $data }
Remove-Item (Join-Path $work 'run') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $state
Write-Output 'torn down'
