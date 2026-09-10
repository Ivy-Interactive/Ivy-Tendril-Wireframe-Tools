<#
.SYNOPSIS
  Give Claude a prompt; get a wireframe.

.DESCRIPTION
  Drives the full loop end to end: scaffolds a project, hands Claude the CLI's own
  agent-readme as its instructions, lets it author the wireframe, then screenshots the
  result and verifies it actually rendered.

  This doubles as the acceptance test for the tool. A wireframe CLI is only as good as
  what an agent can build with nothing but `wireframe agent-readme` in front of it, so
  that is exactly what this gives it -- no extra hints, no example code beyond the
  scaffold.

.PARAMETER Prompt
  What to wireframe, in plain language.

.PARAMETER Path
  Where to build it. Defaults to a timestamped directory under ./wireframes.

.PARAMETER Width / Height
  Screenshot viewport in CSS pixels.

.PARAMETER Model
  Claude model to use. Defaults to the CLI's configured default.

.PARAMETER Serve
  Leave a dev server running at the end instead of exiting.

.PARAMETER SkipAgent
  Scaffold and screenshot without invoking Claude. Useful for testing this script itself.

.EXAMPLE
  ./scripts/agent-wireframe.ps1 "A project management dashboard with a sidebar, a kanban board and a stats row"

.EXAMPLE
  ./scripts/agent-wireframe.ps1 "Mobile checkout flow" -Width 420 -Height 900 -Serve
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Prompt,

    [string]$Path,
    [int]$Width = 1440,
    [int]$Height = 900,
    [string]$Model,
    [switch]$Serve,
    [switch]$SkipAgent,
    [int]$TimeoutMinutes = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------------------
# Locate the wireframe CLI. Prefer a build from this checkout so the script
# always tests the code in front of you, not a stale global install.
# ---------------------------------------------------------------------------
function Resolve-WireframeCli {
    $local = Join-Path $repoRoot 'src/Ivy.Tendril.Wireframe.Console/bin/Debug/net10.0/wireframe.exe'
    if (Test-Path $local) { return $local }

    $onPath = Get-Command 'wireframe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    Write-Host '  building the wireframe CLI...' -ForegroundColor DarkGray
    dotnet build (Join-Path $repoRoot 'src/Ivy.Tendril.Wireframe.Console') -v q --nologo | Out-Null
    if (-not (Test-Path $local)) {
        throw "Could not find or build the wireframe CLI at $local"
    }
    return $local
}

function Assert-ClaudeAvailable {
    if (Get-Command 'claude' -ErrorAction SilentlyContinue) { return }
    throw @'
The `claude` CLI is not on PATH.

Install it with:  npm install -g @anthropic-ai/claude-code
Then sign in:     claude
'@
}

function Write-Step($text) {
    Write-Host ''
    Write-Host "  $text" -ForegroundColor Cyan
}

$wireframe = Resolve-WireframeCli
if (-not $SkipAgent) { Assert-ClaudeAvailable }

if (-not $Path) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $Path = Join-Path $repoRoot "wireframes/$stamp"
}
$Path = [System.IO.Path]::GetFullPath($Path)

Write-Host ''
Write-Host '  wireframe agent' -ForegroundColor Green
Write-Host "  prompt   $Prompt" -ForegroundColor DarkGray
Write-Host "  path     $Path" -ForegroundColor DarkGray
Write-Host "  viewport ${Width}x${Height}" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# 1. Scaffold
# ---------------------------------------------------------------------------
Write-Step 'scaffolding'
& $wireframe setup $Path --quiet
if ($LASTEXITCODE -ne 0) { throw "wireframe setup failed with exit code $LASTEXITCODE" }
Write-Host "  ok" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# 2. Instructions
#
# The agent gets the CLI's own reference and nothing else. If a wireframe cannot
# be built from that alone, agent-readme is what needs fixing -- which is the
# whole point of running this.
# ---------------------------------------------------------------------------
Write-Step 'preparing instructions'
$readmePath = Join-Path $Path 'AGENT.md'
& $wireframe agent-readme --out $readmePath | Out-Null
if ($LASTEXITCODE -ne 0) { throw "wireframe agent-readme failed with exit code $LASTEXITCODE" }
Write-Host "  $([math]::Round((Get-Item $readmePath).Length / 1KB)) KB reference written to AGENT.md" -ForegroundColor DarkGray

$task = @"
Build a wireframe mockup for this brief:

    $Prompt

You are working in: $Path

Read ./AGENT.md first -- it is the complete reference for the CLI and the component
library, and it is the only documentation you need. Then edit src/App.tsx (and add more
files under src/ if that keeps things readable).

How to work:
1. Read AGENT.md.
2. Write src/App.tsx.
3. Run this to render it (the CLI is on your PATH):
       wireframe screenshot "$Path" -w $Width --height $Height
4. LOOK AT the PNG it writes under screenshots/ using the Read tool. Actually open it.
5. Fix what looks wrong -- overlapping elements, empty space, cramped or unbalanced
   layout, missing structure -- and screenshot again. Iterate until it reads like a
   deliberate wireframe of the brief above.

Requirements:
- Fill the ${Width}x${Height} viewport. A wireframe that occupies the top third of the
  page is not finished.
- Use the Tendril components for anything that should look drawn. Plain divs with
  Tailwind utilities are correct for structural layout only.
- Keep SketchProvider, the "tendril" class and signalWireframeReady() in src/main.tsx.
- Do not add dependencies, create a package.json, or run npm/node. There is no package
  manager here and nothing else resolves.
- Do not run ``wireframe serve`` -- it blocks forever. Only ``wireframe screenshot`` is
  allowlisted; use it to check your work.

Stop when the screenshot is a good wireframe of the brief. Then reply with a short summary
of the screens and components you used.
"@

# ---------------------------------------------------------------------------
# 3. Hand off to Claude
# ---------------------------------------------------------------------------
if ($SkipAgent) {
    Write-Step 'skipping the agent (-SkipAgent); using the scaffold as-is'
}
else {
    Write-Step 'running claude'
    Write-Host '  (streaming; this usually takes a few minutes)' -ForegroundColor DarkGray
    Write-Host ''

    # acceptEdits alone lets the agent write files but still prompts for Bash, and a
    # non-interactive run cannot answer that prompt -- the agent then authors the whole
    # wireframe blind, never seeing its own render. Allowlisting just the screenshot
    # command keeps the loop closed without handing over blanket permissions.
    #
    # The CLI's directory goes on PATH so the agent invokes a bare `wireframe`, which the
    # allowlist pattern can match reliably; matching a quoted absolute Windows path is
    # far more fragile.
    $cliDir = Split-Path -Parent $wireframe

    $allowed = @(
        'Bash(wireframe screenshot:*)'
        'Read', 'Write', 'Edit', 'Glob', 'Grep'
    )

    $claudeArgs = @(
        '-p', $task
        '--permission-mode', 'acceptEdits'
        '--add-dir', $Path
        '--allowedTools'
    ) + $allowed
    if ($Model) { $claudeArgs += @('--model', $Model) }

    $job = Start-Job -ScriptBlock {
        param($dir, $cliDir, $claudeArgs)
        $env:PATH = "$cliDir$([IO.Path]::PathSeparator)$env:PATH"
        Set-Location $dir
        & claude @claudeArgs 2>&1
    } -ArgumentList $Path, $cliDir, $claudeArgs

    $finished = Wait-Job $job -Timeout ($TimeoutMinutes * 60)
    if (-not $finished) {
        Stop-Job $job -ErrorAction SilentlyContinue
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        throw "The agent did not finish within $TimeoutMinutes minutes."
    }

    Receive-Job $job | ForEach-Object { Write-Host "  $_" }
    Remove-Job $job -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------------------
# 4. Screenshot and verify
#
# The agent screenshots as it works, but we take the authoritative one here so
# the result does not depend on the agent having remembered to.
# ---------------------------------------------------------------------------
Write-Step 'capturing final screenshot'
& $wireframe screenshot $Path -w $Width --height $Height
if ($LASTEXITCODE -ne 0) { throw "wireframe screenshot failed with exit code $LASTEXITCODE" }

$shot = Join-Path $Path "screenshots/${Width}x${Height}.png"
if (-not (Test-Path $shot)) { throw "Expected a screenshot at $shot but none was written." }

$size = (Get-Item $shot).Length
Write-Step 'result'
Write-Host "  screenshot  $shot" -ForegroundColor DarkGray
Write-Host "  size        $([math]::Round($size / 1KB)) KB" -ForegroundColor DarkGray

# A wireframe that rendered nothing still produces a valid PNG, so check that the file
# is large enough to plausibly contain drawn content. A blank 1440x900 page compresses
# to a few KB; anything real is far bigger.
if ($size -lt 20KB) {
    Write-Host ''
    Write-Host "  WARNING: the PNG is only $([math]::Round($size / 1KB)) KB, which usually means the page rendered blank." -ForegroundColor Yellow
    Write-Host "  Check for build errors:  `"$wireframe`" screenshot `"$Path`"" -ForegroundColor Yellow
    exit 2
}

Write-Host ''
Write-Host '  done' -ForegroundColor Green
Write-Host ''

if ($Serve) {
    Write-Host '  starting the dev server (ctrl+c to stop)' -ForegroundColor Cyan
    Write-Host ''
    & $wireframe serve $Path --open
}
else {
    Write-Host "  preview it with:  `"$wireframe`" serve `"$Path`" --open" -ForegroundColor DarkGray
    Write-Host ''
}
