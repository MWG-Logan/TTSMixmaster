<#
.SYNOPSIS
  Re-vendors the Microsoft WinUI / WinApp CLI agent skills into this repo so every
  agent (Claude Code, Codex, Copilot) working in any worktree has the same tooling.

.DESCRIPTION
  Skills land in two places with identical content:
    .agents/skills/   read by Codex (and other AGENTS.md-style agents)
    .claude/skills/   read by Claude Code
  The winui-dev subagent goes to .claude/agents/winui-dev.md.

  Source: https://github.com/microsoft/winappCli (plugins/winui, plugins/winapp),
  the same content the microsoft/win-dev-skills marketplace installs.

  Skills that don't apply to this project (MAUI, WPF migration) are skipped.
  Pass -Ref to pin a tag/commit; the default is main.

  The winapp CLI itself is a machine prerequisite:
    winget install --id Microsoft.WinAppCli --source winget
#>
param(
    [string]$Ref = "main"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$skip = @("winapp-maui", "winui-wpf-migration")

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("winappcli-" + [guid]::NewGuid())
try {
    git clone --quiet --depth 1 --branch $Ref https://github.com/microsoft/winappCli $tmp 2>$null
    if ($LASTEXITCODE -ne 0) {
        # $Ref may be a commit sha rather than a branch/tag
        git clone --quiet https://github.com/microsoft/winappCli $tmp
        git -C $tmp checkout --quiet $Ref
    }
    $sha = git -C $tmp rev-parse HEAD

    $sources = @(
        (Join-Path $tmp "plugins/winui/agent-plugin/skills"),
        (Join-Path $tmp "plugins/winapp/skills")
    )

    foreach ($target in @(".agents/skills", ".claude/skills")) {
        $dest = Join-Path $repoRoot $target
        New-Item -ItemType Directory -Force $dest | Out-Null
        # Replace only the vendored winui-*/winapp-* skills; leave project-owned skills alone.
        Get-ChildItem $dest -Directory | Where-Object { $_.Name -match '^(winui|winapp)-' } |
            Remove-Item -Recurse -Force
        foreach ($src in $sources) {
            Get-ChildItem $src -Directory | Where-Object { $skip -notcontains $_.Name } |
                ForEach-Object { Copy-Item -Recurse -Force $_.FullName $dest }
        }
    }

    $agents = Join-Path $repoRoot ".claude/agents"
    New-Item -ItemType Directory -Force $agents | Out-Null
    Copy-Item -Force (Join-Path $tmp "plugins/winui/agents/winui-dev.agent.md") (Join-Path $agents "winui-dev.md")

    Set-Content -Encoding utf8 (Join-Path $repoRoot ".agents/skills/VENDORED.md") @"
# Vendored agent skills

winui-* and winapp-* skills are copied from https://github.com/microsoft/winappCli
at commit $sha (MIT). Do not edit them here; re-run ``scripts/sync-agent-skills.ps1``.
The same files are mirrored in ``.claude/skills/``.
"@
    Write-Host "Synced WinUI/WinApp skills from microsoft/winappCli@$sha"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
