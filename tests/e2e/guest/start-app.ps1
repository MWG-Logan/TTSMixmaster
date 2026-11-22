# Starts the pushed, published TTSMixmaster.exe in the Sandbox and prints its PID (for `winapp ui --on sandbox -a <pid>`).
# Win32_Process.Create, like setup.ps1, so `winapp target exec` returns at once. With -Find, prints the PIDs of
# TTSMixmaster processes already running from that folder instead.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppDir, [switch]$Find)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $AppDir 'TTSMixmaster.exe'
if ($Find) {
    Get-CimInstance Win32_Process -Filter "Name = 'TTSMixmaster.exe'" | Where-Object ExecutablePath -eq $exe | ForEach-Object ProcessId
    return
}
if (-not (Test-Path $exe)) { throw "No TTSMixmaster.exe in $AppDir" }
$created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`""; CurrentDirectory = $AppDir }
if ($created.ReturnValue -ne 0) { throw "Couldn't start $exe (Win32_Process.Create $($created.ReturnValue))." }
$created.ProcessId
