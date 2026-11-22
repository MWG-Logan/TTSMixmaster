# UI smoke test for TTSMixmaster: one batch of `winapp ui` (UI Automation) checks against the running app.
# Pattern: .agents/skills/winui-ui-testing. Selectors are AutomationIds.
#
#   tests/e2e/ui-smoke.ps1 -Target sandbox                 # build host-side, run + drive the app in Windows Sandbox
#   tests/e2e/ui-smoke.ps1 -Target local                   # this desktop (prefer sandbox; see AGENTS.md)
#   tests/e2e/ui-smoke.ps1 -Target local -AppExe <publish>\TTSMixmaster.exe   # smoke a published build
#   tests/e2e/ui-smoke.ps1 -Target sandbox -AppPid 1234 -AppScope sandbox     # reuse a live guest app
#
# Writes test-results.json and screenshots (always the initial/final state, plus one per failing check)
# to -ArtifactDirectory. Exit code 1 when any check fails. View the screenshots: UIA can't see clipping or theming.
#
# TODO(after the App UI lands): docs/ui/BRIEF.md was not in this branch's base, so the app-specific checks
# below use placeholder AutomationIds marked TODO-SELECTOR. Replace them with the IDs from the brief and
# remove them from $PendingChecks.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('sandbox', 'local')][string]$Target,
    [int]$AppPid = 0,
    [ValidateSet('sandbox', 'local')][string]$AppScope,
    [string]$AppExe,
    [ValidateNotNullOrEmpty()][string]$WorkflowId = [guid]::NewGuid().ToString('N'),
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot (Join-Path 'artifacts' (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$AppProject = Join-Path $RepoRoot 'src\TTSMixmaster.App'
$ScopeArgs = @(if ($Target -eq 'sandbox') { '--on'; 'sandbox' })
$previousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = $WorkflowId
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$uiStarted = $false
$hwnd = $null

# Checks that exist but can't pass until the real UI and its brief land. Reported as PENDING, not FAIL.
$PendingChecks = @('Collections page loads', 'Settings page round-trips a value')

function Invoke-WinAppChecked {
    param([string[]]$Arguments)
    $global:LASTEXITCODE = $null
    $output = @(& winapp @Arguments)
    $code = $global:LASTEXITCODE
    if ($null -eq $code -or $code -ne 0) {
        throw "winapp $($Arguments -join ' ') failed (exit $code). $($output -join "`n")"
    }
    $output
}

function Invoke-Ui {
    param([string[]]$Arguments, [string]$Window)
    $selector = if ($Window) { @('-w', $Window) } else { @('-a', "$AppPid") }
    Invoke-WinAppChecked (@('ui') + $Arguments + $ScopeArgs + $selector)
}

function Save-Screenshot {
    param([string]$Name)
    $path = Join-Path $ArtifactDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Choose a new evidence path: $path" }
    Invoke-Ui @('screenshot', '--output', $path) -Window $hwnd | Out-Null
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Screenshot was not delivered to the host: $path"
    }
    $screenshots.Add($path)
}

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        & $Script | Out-Null
        $results.Add(@{ name = $Name; status = 'PASS' })
    } catch {
        $status = if ($PendingChecks -contains $Name) { 'PENDING' } else { 'FAIL' }
        $entry = @{ name = $Name; status = $status; detail = "$_" }
        if ($status -eq 'FAIL' -and $hwnd) {
            # Evidence for every failure, captured in the state that failed.
            $safe = ($Name -replace '[^A-Za-z0-9]+', '-').Trim('-')
            try { Save-Screenshot ("fail-{0:D2}-{1}.png" -f $results.Count, $safe); $entry.screenshot = $screenshots[-1] }
            catch { $entry.screenshotError = "$_" }
        }
        $results.Add($entry)
    }
}

try {
    $ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
    if ($AppPid -lt 0) { throw 'AppPid must be positive, or zero to launch.' }

    if ($AppPid -gt 0) {
        if ($AppScope -ne $Target) { throw 'Reusing a PID requires matching -AppScope and -Target from the same live target.' }
    } elseif ($AppExe) {
        # A published build (the folder the installer ships). Local only: the guest can't see host paths.
        if ($Target -ne 'local') { throw '-AppExe runs a host path and needs -Target local (or push the folder to the guest and pass -AppPid).' }
        $AppPid = (Start-Process -FilePath (Resolve-Path $AppExe) -PassThru).Id
        Start-Sleep -Seconds 3
    } else {
        Push-Location $AppProject
        try {
            $launch = Invoke-WinAppChecked (@('run', '.') + $ScopeArgs + @('--detach', '--json')) | ConvertFrom-Json
        } finally { Pop-Location }
        if ($launch.Error -or -not $launch.ProcessId -or [int]$launch.ProcessId -le 0) {
            # Unpackaged guest runs may not report the app PID: find TTSMixmaster with
            # `winapp ui list-windows --on sandbox --json` and rerun with -AppPid <pid> -AppScope sandbox.
            throw "No app ProcessId. $($launch.Error)"
        }
        $AppPid = [int]$launch.ProcessId
        if ($Target -eq 'sandbox' -and ($launch.ProcessScope -ne 'sandbox' -or $launch.UiTargetArgs -ne "--on sandbox -a $AppPid")) {
            throw 'Launch response is missing the expected Sandbox ProcessScope / UiTargetArgs pair.'
        }
        if ($Target -eq 'local' -and $launch.ProcessScope -and $launch.ProcessScope -ne 'local') {
            throw 'Launch response belongs to a different target.'
        }
    }

    $uiStarted = $true
    $windows = @(Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json)
    $main = $windows | Where-Object { $_.hwnd -and $_.hwnd -ne '0' -and $_.title -ne 'PopupHost' } | Select-Object -First 1
    if (-not $main) { throw 'No app window found in the selected target; rediscover after a fresh guest.' }
    $hwnd = [string]$main.hwnd

    # ---- Stable today ---------------------------------------------------------------------------
    Test-UI 'Main window is up' {
        if ([string]::IsNullOrWhiteSpace($main.title)) { throw 'Main window has no title.' }
    }
    Test-UI 'Initial screenshot delivered' { Save-Screenshot '01-initial.png' }

    # ---- App-specific (TODO-SELECTOR: replace with AutomationIds from docs/ui/BRIEF.md) -------------
    Test-UI 'Collections page loads' {
        Invoke-Ui @('wait-for', 'TODO-SELECTOR-NavCollections', '-t', '5000')
        Invoke-Ui @('invoke', 'TODO-SELECTOR-NavCollections')
        Invoke-Ui @('wait-for', 'TODO-SELECTOR-CollectionsList', '-t', '5000')
    }
    Test-UI 'Settings page round-trips a value' {
        Invoke-Ui @('invoke', 'TODO-SELECTOR-NavSettings')
        Invoke-Ui @('wait-for', 'TODO-SELECTOR-TxtDownloadPath', '-t', '3000')
        # set-value doesn't commit an x:Bind TwoWay TextBox until focus moves: invoke Save (or focus away) first.
        Invoke-Ui @('set-value', 'TODO-SELECTOR-TxtAudioQuality', '160')
        Invoke-Ui @('invoke', 'TODO-SELECTOR-BtnSaveSettings')
        Invoke-Ui @('wait-for', 'TODO-SELECTOR-TxtAudioQuality', '--value', '160', '-t', '2000')
    }

    # ---- Conventions (AGENTS.md: AutomationId on every interactive control) ------------------------
    Test-UI 'App controls have AutomationIds' {
        $inspection = Invoke-Ui @('inspect', '--interactive', '--json') -Window $hwnd | ConvertFrom-Json
        function Get-Elements($nodes) { foreach ($node in $nodes) { if ($null -ne $node) { $node; Get-Elements $node.children } } }
        $all = @(Get-Elements @($inspection.windows | ForEach-Object { $_.elements }))
        if (-not $all.Count) { throw 'Inspection returned no elements; not an accessibility PASS.' }
        $controls = @($all | Where-Object {
            # Generated list items (one per collection/track) are data, not authored controls: not audited.
            $_.type -match 'Button|TextBox|ComboBox|CheckBox|ToggleSwitch|TabItem|Edit' -and
            $_.name -notmatch 'Minimize|Maximize|Close|System' -and
            $_.className -notmatch 'PickerHost|#32770|CabinetWClass'
        })
        if (-not $controls.Count) { throw 'No app controls were audited; adjust the filter rather than passing.' }
        $missing = @($controls | Where-Object { -not $_.automationId })
        if ($missing.Count) { throw "Missing AutomationIds: $(($missing | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ', ')" }
    }
    Test-UI 'Final screenshot delivered' { Save-Screenshot '99-final.png' }
} catch {
    $results.Add(@{ name = 'Target setup / test execution'; status = 'FAIL'; detail = "$_" })
} finally {
    try {
        if ($uiStarted) { Invoke-WinAppChecked (@('ui', 'yield') + $ScopeArgs) | Out-Null }
    } catch {
        $results.Add(@{ name = 'Release UI workflow'; status = 'FAIL'; detail = "$_" })
    } finally {
        $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflowId
    }
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$pending = @($results | Where-Object { $_.status -eq 'PENDING' }).Count
$report = [ordered]@{
    target = $Target; appPid = $AppPid; workflowId = $WorkflowId
    passed = @($results | Where-Object { $_.status -eq 'PASS' }).Count; failed = $failed; pending = $pending
    results = @($results.ToArray()); screenshots = @($screenshots.ToArray())
}
$resultsFile = Join-Path $ArtifactDirectory 'test-results.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultsFile -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed | Pending: $pending | Results: $resultsFile"
if ($failed) { exit 1 }
exit 0
