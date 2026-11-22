# UI smoke test for TTSMixmaster: one batch of `winapp ui` (UI Automation) checks against the running app.
# Pattern: .agents/skills/winui-ui-testing. Selectors are AutomationIds (docs/ui/BRIEF.md, the XAML).
#
#   tests/e2e/ui-smoke.ps1 -Target sandbox                          # build host-side, run + drive the app in Windows Sandbox
#   tests/e2e/ui-smoke.ps1 -Target sandbox -PublishDir <folder>     # a published build (the folder the installer ships)
#   tests/e2e/ui-smoke.ps1 -Target sandbox -PublishDir <folder> -DevSource   # + Build and Export to TTS (see below)
#   tests/e2e/ui-smoke.ps1 -Target local -AppExe <publish>\TTSMixmaster.exe  # this desktop (prefer sandbox; see AGENTS.md)
#   tests/e2e/ui-smoke.ps1 -Target sandbox -AppPid 1234 -AppScope sandbox     # reuse a live guest app
#
# Checks: every page opens; Settings shows an unconfigured service as "Not set up" with Test connection disabled,
# a failing Azure Test connection shows the error InfoBar, an edited value is saved to config.json and survives
# leaving the page. With -DevSource (Sandbox only) it also builds the dev collection end to end (navigating away
# mid-build and finding it under Builds > In progress) and exports it:
#   dotnet publish src/TTSMixmaster.App -c Release -p:Platform=x64 -p:TtsmDevSource=true -o <folder>
#   copy tests/.tools/{yt-dlp,ffmpeg,ffprobe}.exe into <folder>   (scripts/fetch-tools.ps1 -OutputFolder tests/.tools)
# and needs node (with `npm install -g azurite`) on the host: node.exe and Azurite are copied into the guest, where
# guest/setup.ps1 starts Azurite and a media server on loopback and writes the guest's config.json and
# dev-collection.json (an existing guest TTSMixmaster data folder is moved aside and restored by guest/teardown.ps1).
# Nothing reaches a real music service or Azure.
#
# Writes test-results.json and screenshots (one per page, initial/final, plus one per failing check) to
# -ArtifactDirectory. Every check is required: exit code 1 when any fails. Look at the screenshots too: UI
# Automation can't see clipping, overlap or theming problems. The script never stops or closes the Sandbox; it
# stops only the app it launched.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('sandbox', 'local')][string]$Target,
    [int]$AppPid = 0,
    [ValidateSet('sandbox', 'local')][string]$AppScope,
    [string]$AppExe,
    [string]$PublishDir,
    [switch]$DevSource,
    [ValidateNotNullOrEmpty()][string]$WorkflowId = [guid]::NewGuid().ToString('N'),
    [string]$ArtifactDirectory # default: tests/e2e/artifacts/<timestamp>
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 doesn't set $PSScriptRoot for param defaults, so the default is resolved here.
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $PSScriptRoot (Join-Path 'artifacts' (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$PSNativeCommandUseErrorActionPreference = $false
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$AppProject = Join-Path $RepoRoot 'src\TTSMixmaster.App'
$ScopeArgs = @(if ($Target -eq 'sandbox') { '--on'; 'sandbox' })
$GuestWork = 'ttsm-e2e' # relative to the guest's managed work area (winapp target push/exec)
$previousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = $WorkflowId
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$uiStarted = $false
$launchedHere = $false
$guestReady = $false
$guestPath = $null
$hwnd = $null
$startedProcesses = [Collections.Generic.List[object]]::new()   # Invoke-Bounded's processes: Id, Start

if ($DevSource -and ($Target -ne 'sandbox' -or -not $PublishDir)) { throw '-DevSource needs -Target sandbox and a dev-source -PublishDir.' }

# One argument for a Windows command line (CommandLineToArgvW rules), so embedded quotes survive.
function ConvertTo-CommandLineArgument {
    param([string]$Value)
    if ($Value -and $Value -notmatch '[\s"]') { return $Value }
    '"' + (($Value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

# Runs an external program under a deadline and returns its exit code and output. No external command may block the
# run: on timeout its whole process tree is killed and the call throws. Output comes through this call's own pipes,
# and once the program exits, a helper it left running can't keep the call waiting for them to close.
function Invoke-Bounded {
    param([string]$FilePath, [string[]]$Arguments, [int]$TimeoutSec)
    $psi = New-Object Diagnostics.ProcessStartInfo $FilePath
    $psi.Arguments = (@($Arguments) | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' '
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [Console]::OutputEncoding
    $psi.StandardErrorEncoding = [Console]::OutputEncoding
    $process = [Diagnostics.Process]::Start($psi)
    $startedProcesses.Add([pscustomobject]@{ Id = $process.Id; Start = $process.StartTime })
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSec * 1000)) {
            Stop-ProcessTree $process.Id
            throw "$([IO.Path]::GetFileName($FilePath)) $($Arguments -join ' ') timed out after $TimeoutSec s and was stopped."
        }
        if (-not [Threading.Tasks.Task]::WaitAll(@($stdout, $stderr), 10000)) {
            Write-Warning "$([IO.Path]::GetFileName($FilePath)) exited, but a process it started still holds its output; continuing without the rest."
        }
        $lines = { param($t) if ($t.IsCompleted) { @($t.Result -split '\r?\n' | Where-Object { $_ -ne '' }) } else { @() } }
        [pscustomobject]@{ Code = $process.ExitCode; Output = @(& $lines $stdout); Error = @(& $lines $stderr) }
    } finally { $process.Dispose() }
}

# taskkill /T: the process and everything it started (Windows PowerShell 5.1 has no Kill(entireProcessTree)).
function Stop-ProcessTree {
    param([int]$ProcessId)
    $null = & taskkill.exe /PID $ProcessId /T /F 2>&1
}

# At the end: anything this run started that is still running (a stalled or left-behind child), with its descendants.
# A process counts only if it was created after its parent started, and a recorded PID now held by a different
# process (reused) is skipped, so nothing unrelated is touched.
function Stop-LeftoverProcesses {
    $running = @(Get-CimInstance Win32_Process)
    $byId = @{}
    foreach ($p in $running) { $byId[[int]$p.ProcessId] = $p }
    $ours = @{}   # process id -> its start time
    $stop = [Collections.Generic.List[object]]::new()
    foreach ($s in $startedProcesses) {
        $now = $byId[$s.Id]
        if ($now -and [math]::Abs(($now.CreationDate - $s.Start).TotalSeconds) -gt 1) { continue }
        $ours[$s.Id] = $s.Start
        if ($now) { $stop.Add($now) }
    }
    do {
        $added = $false
        foreach ($p in $running) {
            $id = [int]$p.ProcessId; $parent = [int]$p.ParentProcessId
            if ($ours.ContainsKey($parent) -and -not $ours.ContainsKey($id) -and $p.CreationDate -ge $ours[$parent]) {
                $ours[$id] = $p.CreationDate; $stop.Add($p); $added = $true
            }
        }
    } while ($added)
    foreach ($p in $stop) {
        Write-Verbose "Stopping leftover process $($p.ProcessId) ($($p.Name))"
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

$WinApp = (Get-Command winapp -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source

function Invoke-WinAppChecked {
    param([string[]]$Arguments, [int]$TimeoutSec = 120)
    $r = Invoke-Bounded $WinApp $Arguments $TimeoutSec
    if ($r.Code -ne 0) {
        throw "winapp $($Arguments -join ' ') failed (exit $($r.Code)). $(($r.Output + $r.Error) -join "`n")"
    }
    $r.Output
}

function Invoke-Ui {
    param([string[]]$Arguments)
    $selector = if ($hwnd) { @('-w', $hwnd) } else { @('-a', "$AppPid") }
    # A UI wait (-t <ms>) bounds itself; its process gets a minute more than that before it is stopped.
    $t = [array]::IndexOf($Arguments, '-t')
    $timeoutSec = if ($t -ge 0) { [int][math]::Ceiling([int]$Arguments[$t + 1] / 1000) + 60 } else { 120 }
    Invoke-WinAppChecked (@('ui') + $Arguments + $ScopeArgs + $selector) -TimeoutSec $timeoutSec
}

# Runs a PowerShell command in the guest (Sandbox) and returns its output lines. `winapp target exec` has no
# timeout of its own: a command that hangs is stopped and fails the run instead of blocking it.
function Invoke-Guest {
    param([string]$Command, [int]$TimeoutSec = 300)
    $r = Invoke-Bounded $WinApp @('target', 'exec', 'sandbox', '--', 'powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $Command) $TimeoutSec
    if ($r.Code -ne 0) { throw "Guest command failed (exit $($r.Code)): $Command`n$(($r.Output + $r.Error) -join "`n")" }
    $r.Output
}

function Get-UiProperty {
    param([string]$Selector, [string]$Property)
    $json = (Invoke-Ui @('get-property', $Selector, '-p', $Property, '--json')) -join "`n" | ConvertFrom-Json
    "$($json.properties.$Property)"
}

# Settings sections are SettingsExpanders: the AutomationId is on a Group whose first Button is the header toggle.
# Expands the section unless <Child> is already showing.
function Open-Section {
    param([string]$Section, [string]$Child)
    try { Invoke-Ui @('wait-for', $Child, '-t', '1000') | Out-Null; return } catch { }
    $header = ((Invoke-Ui @('search', '--type', 'Button', '--root', $Section, '--max', '1', '--json')) -join "`n" | ConvertFrom-Json).matches[0].selector
    if (-not $header) { throw "No header button in $Section." }
    Invoke-Ui @('invoke', $header) | Out-Null
    Invoke-Ui @('wait-for', $Child, '-t', '5000') | Out-Null
}

# Generated list items have no AutomationId: find one by (part of) its name inside a list and invoke it.
function Invoke-ListItem {
    param([string]$List, [string]$Name)
    Invoke-Ui @('wait-for', $Name, '--root', $List, '-t', '30000') | Out-Null
    $item = ((Invoke-Ui @('search', $Name, '--type', 'ListItem', '--root', $List, '--json')) -join "`n" | ConvertFrom-Json).matches[0].selector
    if (-not $item) { throw "No list item '$Name' in $List." }
    Invoke-Ui @('invoke', $item) | Out-Null
}

function Save-Screenshot {
    param([string]$Name)
    $path = Join-Path $ArtifactDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Choose a new evidence path: $path" }
    Invoke-Ui @('screenshot', '--output', $path) | Out-Null
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
        $entry = @{ name = $Name; status = 'FAIL'; detail = "$_" }
        if ($hwnd) {
            # Evidence for every failure, captured in the state that failed.
            $safe = ($Name -replace '[^A-Za-z0-9]+', '-').Trim('-')
            try { Save-Screenshot ("fail-{0:D2}-{1}.png" -f $results.Count, $safe); $entry.screenshot = $screenshots[-1] }
            catch { $entry.screenshotError = "$_" }
        }
        $results.Add($entry)
    }
}

# AGENTS.md: AutomationId on every interactive control of the current page.
function Assert-AutomationIds {
    # The full tree (not --interactive, which flattens it) so each element's real parent is known.
    $inspection = Invoke-Ui @('inspect', '--depth', '60', '--json') | ConvertFrom-Json
    function Get-Elements($nodes, $parent) {
        foreach ($node in $nodes) {
            if ($null -eq $node) { continue }
            $node | Add-Member -NotePropertyName parentId -NotePropertyValue $parent.automationId -Force
            $node | Add-Member -NotePropertyName parentName -NotePropertyValue $parent.name -Force
            $node
            Get-Elements $node.children $node
        }
    }
    $all = @(Get-Elements @($inspection.windows | ForEach-Object { $_.elements }) $null)
    if (-not $all.Count) { throw 'Inspection returned no elements; not an accessibility PASS.' }
    $controls = @($all | Where-Object {
        # Generated list items (one per collection/track) are data, not authored controls: not audited.
        $_.type -match 'Button|TextBox|ComboBox|CheckBox|ToggleSwitch|TabItem|Edit' -and
        $_.name -notmatch 'Minimize|Maximize|Close|System' -and
        $_.className -notmatch 'PickerHost|#32770|CabinetWClass'
    })
    if (-not $controls.Count) { throw 'No app controls were audited; adjust the filter rather than passing.' }
    # A SettingsExpander's header toggle is a template part of the toolkit control: the page's AutomationId sits on
    # the expander (a Group of the same name), and the header is reached through it (Open-Section, --root).
    $missing = @($controls | Where-Object {
        -not $_.automationId -and -not ($_.type -eq 'Button' -and $_.parentId -and $_.parentName -eq $_.name)
    })
    if ($missing.Count) { throw "Missing AutomationIds: $(($missing | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ', ')" }
}

function Open-Page {
    param([string]$NavId, [string]$Landmark, [string]$Shot)
    Invoke-Ui @('invoke', $NavId) | Out-Null
    Invoke-Ui @('wait-for', $Landmark, '-t', '10000') | Out-Null
    Save-Screenshot $Shot
}

# node.exe, Azurite, the fixtures and a 3 s tone for the guest (DevSource).
function New-GuestPayload {
    $payload = Join-Path $ArtifactDirectory 'guest-payload'
    New-Item -ItemType Directory -Force $payload | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'guest\*') $payload
    $node = (Get-Command node -ErrorAction Stop).Source
    Copy-Item $node $payload
    # Beside the azurite-blob shim on PATH (npm's global prefix), else npm's global root.
    $shim = Get-Command azurite-blob -ErrorAction SilentlyContinue | Select-Object -First 1
    $azurite = if ($shim) { Join-Path (Split-Path $shim.Source) 'node_modules\azurite' } else { Join-Path (& npm root -g) 'azurite' }
    if (-not (Test-Path (Join-Path $azurite 'dist\src\blob\main.js'))) { throw "Azurite not found at $azurite. Run: npm install -g azurite" }
    Compress-Archive -Path (Join-Path $azurite '*') -DestinationPath (Join-Path $payload 'azurite.zip')
    $ffmpeg = Join-Path $PublishDir 'ffmpeg.exe'
    if (-not (Test-Path $ffmpeg)) { throw "No ffmpeg.exe in $PublishDir. Copy tests/.tools/{yt-dlp,ffmpeg,ffprobe}.exe there." }
    $tone = Invoke-Bounded $ffmpeg @('-nostdin', '-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'sine=frequency=440:duration=3',
        '-c:a', 'libmp3lame', '-b:a', '192k', (Join-Path $payload 'tone.mp3')) 120
    if ($tone.Code -ne 0) { throw "Could not generate the test tone. $($tone.Error -join ' ')" }
    $payload
}

try {
    $ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
    New-Item -ItemType Directory -Force -Path $ArtifactDirectory | Out-Null
    if ($AppPid -lt 0) { throw 'AppPid must be positive, or zero to launch.' }

    # Sandbox + published folder: copy the app and the guest scripts in first. Pushes fail while the fixture servers
    # run (the guest reports the work area busy), so everything goes in before setup.ps1 starts them.
    $guestApp = $null
    if ($Target -eq 'sandbox' -and $PublishDir -and $AppPid -eq 0) {
        $payload = if ($DevSource) { New-GuestPayload } else {
            $p = Join-Path $ArtifactDirectory 'guest-payload'; New-Item -ItemType Directory -Force $p | Out-Null
            Copy-Item (Join-Path $PSScriptRoot 'guest\*') $p; $p
        }
        $guestPath = ((Invoke-WinAppChecked @('target', 'push', 'sandbox', $payload, $GuestWork, '--json') -TimeoutSec 600) -join "`n" | ConvertFrom-Json).targetPath
        $guestApp = ((Invoke-WinAppChecked @('target', 'push', 'sandbox', (Resolve-Path $PublishDir).Path, "$GuestWork-app", '--json') -TimeoutSec 600) -join "`n" | ConvertFrom-Json).targetPath
    }
    if ($DevSource) {
        $guestReady = $true # from here on, teardown runs
        Invoke-Guest "& '$guestPath\setup.ps1'" -TimeoutSec 600 | Write-Verbose
    }

    if ($AppPid -gt 0) {
        if ($AppScope -ne $Target) { throw 'Reusing a PID requires matching -AppScope and -Target from the same live target.' }
    } elseif ($AppExe) {
        # Local only: the guest can't see host paths (use -PublishDir for the Sandbox).
        if ($Target -ne 'local') { throw '-AppExe runs a host path and needs -Target local; use -PublishDir for the Sandbox.' }
        $AppPid = (Start-Process -FilePath (Resolve-Path $AppExe) -PassThru).Id
        $launchedHere = $true
        Start-Sleep -Seconds 3
    } elseif ($guestApp) {
        # `winapp run <folder>` expects a packaged layout; the app is unpackaged, so start the pushed exe directly.
        $AppPid = [int](@(Invoke-Guest "& '$guestPath\start-app.ps1' -AppDir '$guestApp'")[-1])
        $launchedHere = $true
    } elseif ($PublishDir) {
        throw '-PublishDir runs in the Sandbox; for this desktop use -AppExe <folder>\TTSMixmaster.exe.'
    } else {
        $launch = (Invoke-WinAppChecked (@('run', $AppProject) + $ScopeArgs + @('--detach', '--json')) -TimeoutSec 900) -join "`n" | ConvertFrom-Json
        if ($launch.Error -or -not $launch.ProcessId -or [int]$launch.ProcessId -le 0) {
            # Unpackaged guest runs may not report the app PID: find TTSMixmaster with
            # `winapp ui list-windows --on sandbox --json` and rerun with -AppPid <pid> -AppScope sandbox.
            throw "No app ProcessId. $($launch.Error)"
        }
        $AppPid = [int]$launch.ProcessId
        $launchedHere = $true
        if ($Target -eq 'sandbox' -and ($launch.ProcessScope -ne 'sandbox' -or $launch.UiTargetArgs -ne "--on sandbox -a $AppPid")) {
            throw 'Launch response is missing the expected Sandbox ProcessScope / UiTargetArgs pair.'
        }
        if ($Target -eq 'local' -and $launch.ProcessScope -and $launch.ProcessScope -ne 'local') {
            throw 'Launch response belongs to a different target.'
        }
    }

    $uiStarted = $true
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $windows = @(Invoke-Ui @('list-windows', '--json') | ConvertFrom-Json)
        $main = $windows | Where-Object { $_.hwnd -and $_.hwnd -ne '0' -and $_.title -eq 'TTSMixmaster' } | Select-Object -First 1
        if (-not $main) { Start-Sleep -Milliseconds 500 }
    } while (-not $main -and (Get-Date) -lt $deadline)
    if (-not $main) { throw 'No TTSMixmaster window found in the selected target; rediscover after a fresh guest.' }
    $hwnd = [string]$main.hwnd

    # ---- Shell and navigation ------------------------------------------------------------------------
    Test-UI 'Main window is up' {
        Invoke-Ui @('wait-for', 'NavView', '-t', '10000')
        Save-Screenshot '01-initial.png'
    }
    # With no music service set up the page is the first-run "Connect a music service" state, without search;
    # with one (always with -DevSource) it has the search box.
    Test-UI 'Collections page opens' {
        Invoke-Ui @('invoke', 'NavCollections') | Out-Null
        if ($DevSource) { Invoke-Ui @('wait-for', 'SearchBox', '-t', '10000') | Out-Null }
        else {
            try { Invoke-Ui @('wait-for', 'OpenSettingsFromEmpty', '-t', '10000') | Out-Null }
            catch { Invoke-Ui @('wait-for', 'SearchBox', '-t', '1000') | Out-Null } # this guest has a service set up
        }
        Save-Screenshot '02-collections.png'
    }
    Test-UI 'Builds page opens' { Open-Page 'NavBuilds' 'RefreshBuilds' '03-builds.png' }
    Test-UI 'Settings page opens' { Open-Page 'NavSettings' 'LastFmSettings' '04-settings.png' }

    # ---- Settings ------------------------------------------------------------------------------------
    Test-UI 'An unconfigured service cannot be tested' {
        Open-Section 'SpotifySettings' 'TestSpotify'
        $enabled = Get-UiProperty 'TestSpotify' 'IsEnabled'
        if ($enabled -ne 'False') { throw "Test connection is enabled for unconfigured Spotify (IsEnabled=$enabled)." }
        Invoke-Ui @('wait-for', 'Not set up', '--root', 'SpotifySettings', '-t', '3000') | Out-Null
    }
    Test-UI 'Azure Test connection shows the error InfoBar' {
        Open-Section 'AzureAdvanced' 'AzureContainerName' # the connection-string fallback
        if (-not $DevSource) {
            # Nothing listens on port 9: the SDK's connection attempt fails at once on loopback.
            Invoke-Ui @('set-value', 'AzureConnectionString', 'DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:9/devstoreaccount1;') | Out-Null
        }
        Invoke-Ui @('set-value', 'AzureContainerName', 'e2e-missing') | Out-Null
        Invoke-Ui @('wait-for', 'TestAzure', '-t', '5000') | Out-Null
        Invoke-Ui @('invoke', 'TestAzure') | Out-Null
        Invoke-Ui @('wait-for', 'AzureTestResult', '-t', '90000') | Out-Null
        Invoke-Ui @('wait-for', "Couldn't connect to Azure Storage", '--root', 'AzureTestResult', '-t', '90000') | Out-Null
        Invoke-Ui @('scroll-into-view', 'AzureTestResult') | Out-Null # evidence: the InfoBar in the screenshot
        Save-Screenshot '05-azure-test-error.png'
    }
    if ($DevSource) {
        Test-UI 'Azure Test connection succeeds against Azurite' {
            Invoke-Ui @('set-value', 'AzureContainerName', 'tts-audio') | Out-Null
            Invoke-Ui @('invoke', 'TestAzure') | Out-Null
            Invoke-Ui @('wait-for', 'Connected to Azure Storage', '--root', 'AzureTestResult', '-t', '60000') | Out-Null
            Invoke-Ui @('scroll-into-view', 'AzureTestResult') | Out-Null
            Save-Screenshot '06-azure-test-ok.png'
        }
    }
    Test-UI 'An edited setting is saved and survives leaving the page' {
        Open-Section 'LastFmSettings' 'LastfmUsername'
        Invoke-Ui @('set-value', 'LastfmUsername', 'e2e-user') | Out-Null
        Invoke-Ui @('invoke', 'NavBuilds') | Out-Null # leaving Settings saves at once
        Invoke-Ui @('wait-for', 'RefreshBuilds', '-t', '10000') | Out-Null
        Invoke-Ui @('invoke', 'NavSettings') | Out-Null
        Invoke-Ui @('wait-for', 'LastFmSettings', '-t', '10000') | Out-Null
        Open-Section 'LastFmSettings' 'LastfmUsername'
        Invoke-Ui @('wait-for', 'LastfmUsername', '--value', 'e2e-user', '-t', '5000') | Out-Null
        if ($Target -eq 'sandbox') {
            # Single quotes only: Windows PowerShell 5.1 drops embedded double quotes when calling winapp.
            $saved = (Invoke-Guest '(Get-Content (Join-Path $env:LOCALAPPDATA ''TTSMixmaster\config.json'') -Raw | ConvertFrom-Json).lastfm_username') -join ''
            if ($saved.Trim() -ne 'e2e-user') { throw "config.json has lastfm_username '$saved'." }
        }
    }
    Test-UI 'Settings controls have AutomationIds' { Assert-AutomationIds }

    # ---- Build and Export to TTS (dev-source build, Sandbox) -----------------------------------------
    if ($DevSource) {
        Test-UI 'The dev collection opens' {
            Open-Page 'NavCollections' 'CollectionsList' '07-collections-dev.png' | Out-Null
            Invoke-ListItem 'CollectionsList' 'E2E Tavern'
            Invoke-Ui @('wait-for', 'CollectionTitle', '--value', 'E2E Tavern', '-t', '15000') | Out-Null
            Invoke-Ui @('wait-for', 'Opening Tone', '--type', 'ListItem', '--root', 'TrackList', '-t', '15000') | Out-Null
        }
        # A build keeps running on other pages: Builds lists it under In progress (the Slow Tone track is served
        # with a 15 s delay, so it is still running) and the row leads back to the same collection page.
        Test-UI 'Navigate away mid-build, find it in Builds' {
            Invoke-Ui @('invoke', 'BuildButton') | Out-Null
            Invoke-Ui @('wait-for', 'CancelBuildButton', '-t', '10000') | Out-Null
            Save-Screenshot '08a-build-running.png'
            Invoke-Ui @('invoke', 'NavBuilds') | Out-Null
            Invoke-Ui @('wait-for', 'InProgressList', '-t', '10000') | Out-Null
            Invoke-Ui @('wait-for', 'CancelActiveBuild-E2E Tavern', '-t', '10000') | Out-Null
            $nav = Get-UiProperty 'NavBuilds' 'Name'
            if ($nav -ne 'Builds, 1 running') { throw "The Builds nav item reads '$nav', not 'Builds, 1 running' (the badge)." }
            Save-Screenshot '08b-builds-in-progress.png'
            Invoke-ListItem 'InProgressList' 'E2E Tavern'
            Invoke-Ui @('wait-for', 'CollectionTitle', '--value', 'E2E Tavern', '-t', '10000') | Out-Null
        }
        Test-UI 'Build downloads, uploads and generates' {
            Invoke-Ui @('wait-for', 'Ready for Tabletop Simulator', '--root', 'BuildResult', '-t', '240000') | Out-Null
            Invoke-Ui @('wait-for', 'ExportToTtsButton', '-t', '10000') | Out-Null
            Save-Screenshot '08-build-done.png'
        }
        Test-UI 'Export to TTS writes the saved object' {
            Invoke-Ui @('invoke', 'ExportToTtsButton') | Out-Null
            Invoke-Ui @('wait-for', 'ExportSucceeded', '-t', '30000') | Out-Null
            Invoke-Ui @('wait-for', 'Exported to Tabletop Simulator', '--root', 'ExportSucceeded', '-t', '5000') | Out-Null
            $files = @(Invoke-Guest "Get-ChildItem '$guestPath\run\Saved Objects\TTSMixmaster' -File | ForEach-Object Name")
            if (-not ($files -match '\.json$') -or -not ($files -match '\.png$')) { throw "Saved Objects has: $($files -join ', ')" }
            Save-Screenshot '09-exported.png'
        }
        Test-UI 'Collection controls have AutomationIds' { Assert-AutomationIds }
        Test-UI 'The build is listed on the Builds page' {
            Open-Page 'NavBuilds' 'BuildsList' '10-builds-after.png' | Out-Null
            Invoke-Ui @('wait-for', 'E2E Tavern', '--root', 'BuildsList', '-t', '10000') | Out-Null
            # Finished: out of In progress (the section is collapsed) and off the nav badge.
            $nav = Get-UiProperty 'NavBuilds' 'Name'
            if ($nav -ne 'Builds') { throw "The Builds nav item still reads '$nav' after the build finished." }
            try { Invoke-Ui @('wait-for', 'InProgressList', '-t', '1000') | Out-Null; $listed = $true } catch { $listed = $false }
            if ($listed) { throw 'The finished build is still listed under In progress.' }
        }
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
    # Stop only the app this run started (never the Sandbox), then undo the guest fixtures.
    if ($launchedHere -and $AppPid -gt 0) {
        try {
            if ($Target -eq 'sandbox') { Invoke-Guest "Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue" | Out-Null }
            else { Stop-Process -Id $AppPid -Force -ErrorAction SilentlyContinue }
        } catch { $results.Add(@{ name = 'Stop the app'; status = 'FAIL'; detail = "$_" }) }
    }
    if ($guestReady) {
        try { Invoke-Guest "& '$guestPath\teardown.ps1'" | Write-Verbose }
        catch { $results.Add(@{ name = 'Guest teardown'; status = 'FAIL'; detail = "$_" }) }
    }
    # Nothing this run started may outlive it (and keep a caller waiting on the script).
    try { Stop-LeftoverProcesses } catch { Write-Warning "Couldn't check for leftover processes: $_" }
}

$failed = @($results | Where-Object { $_.status -ne 'PASS' }).Count
$report = [ordered]@{
    target = $Target; appPid = $AppPid; workflowId = $WorkflowId; devSource = [bool]$DevSource
    passed = @($results | Where-Object { $_.status -eq 'PASS' }).Count; failed = $failed
    results = @($results.ToArray()); screenshots = @($screenshots.ToArray())
}
$resultsFile = Join-Path $ArtifactDirectory 'test-results.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultsFile -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed | Results: $resultsFile"
if ($failed) { exit 1 }
exit 0
