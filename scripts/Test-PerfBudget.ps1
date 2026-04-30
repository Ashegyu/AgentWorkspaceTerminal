<#
.SYNOPSIS
ADR-008 performance gate. Runs the awt-perfprobe subcommands, parses their JSON
output, and compares each measured metric against (a) the hard ADR-008 ceiling
and (b) 1.5× the saved baseline value from baseline.json. Either gate failing
fails the script (exit 1).

.DESCRIPTION
Day-60 deliverable for MVP-8. The probe metrics (rss 1-pane, rss 4-pane,
gc-idle, zombies) run in <2 minutes total and are gated on every CI build.
BenchmarkDotNet metrics (PtyReadWriteBench, BurstRenderBench, Policy/Redaction)
are NOT run here — they are too long for a per-commit gate and are recorded as
manual measurements in baseline.json (use Run-Benchmarks.ps1 separately).

.PARAMETER BaselinePath
Path to baseline.json. Defaults to src/AgentWorkspace.Benchmarks/Mvp8/baseline.json.

.PARAMETER GcIdleSeconds
Seconds to sample for the GC Gen2 idle test. CI default 30s; local default 60s.

.PARAMETER SkipBuild
Skip the dotnet build step (assume PerfProbe is already published).

.OUTPUTS
Single-line JSON summary of all gates on stdout (last line). Exit 0 = all pass,
1 = at least one regression / threshold breach.
#>

[CmdletBinding()]
param(
    [string] $BaselinePath = (Join-Path $PSScriptRoot '..' 'src/AgentWorkspace.Benchmarks/Mvp8/baseline.json'),
    [int]    $GcIdleSeconds = 30,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..')
$probeProj  = Join-Path $repoRoot 'src/AgentWorkspace.PerfProbe/AgentWorkspace.PerfProbe.csproj'
$probeExe   = Join-Path $repoRoot 'src/AgentWorkspace.PerfProbe/bin/Release/net10.0-windows/awt-perfprobe.exe'

if (-not $SkipBuild) {
    Write-Host "==> dotnet build PerfProbe (Release)" -ForegroundColor Cyan
    dotnet build $probeProj -c Release -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
}
if (-not (Test-Path $probeExe)) { throw "awt-perfprobe.exe not found at $probeExe" }

if (-not (Test-Path $BaselinePath)) { throw "baseline.json not found at $BaselinePath" }
$baseline = Get-Content $BaselinePath -Raw | ConvertFrom-Json

function Invoke-Probe {
    param([string[]] $ProbeArgs, [string] $Label)
    Write-Host "==> awt-perfprobe $($ProbeArgs -join ' ')" -ForegroundColor Cyan
    # Probe writes one JSON line on stdout. cmd.exe ConPTY children can interleave their own
    # prompt text (e.g. "C:\path>") on the same line, so we extract the JSON object substring
    # rather than requiring the line to start with '{'.
    $raw = & $probeExe @ProbeArgs 2>&1
    $candidates = $raw | ForEach-Object {
        $line = "$_"
        $start = $line.IndexOf('{')
        $end   = $line.LastIndexOf('}')
        if ($start -ge 0 -and $end -gt $start) {
            $line.Substring($start, $end - $start + 1)
        }
    } | Where-Object { $_ }
    $jsonLine = $candidates | Select-Object -Last 1
    if (-not $jsonLine) {
        throw "${Label}: no JSON object in probe output. Raw:`n$raw"
    }
    return ($jsonLine | ConvertFrom-Json)
}

# ── Run the four probe subcommands ──────────────────────────────────────────
$rss1 = Invoke-Probe @('rss', '--panes', '1', '--warmup-sec', '3', '--sample-sec', '5') 'rss-1-pane'
$rss4 = Invoke-Probe @('rss', '--panes', '4', '--warmup-sec', '3', '--sample-sec', '5') 'rss-4-pane'
$gc   = Invoke-Probe @('gc-idle', '--seconds', "$GcIdleSeconds")                       'gc-idle'
$zomb = Invoke-Probe @('zombies', '--panes', '4', '--settle-ms', '500')                'zombies'

# ── Measured-value table ────────────────────────────────────────────────────
$measured = [ordered]@{
    onePaneRssDeltaMb    = [double]$rss1.deltaMb
    fourPaneIdleRssMb    = [double]$rss4.peakMb
    gcGen2PerMinuteIdle  = [double]$gc.gen2PerMinute
    zombieChildren       = [int]   $zomb.zombieCount
}

# ── Evaluate gates ──────────────────────────────────────────────────────────
$results = @()
$failed  = $false

foreach ($metric in $measured.Keys) {
    $actual    = $measured[$metric]
    $base      = $baseline.metrics.$metric
    $hardCap   = $baseline.thresholds.$metric

    # Regression gate: 1.5× baseline (only if baseline is set & non-zero).
    $regressionGate = $null
    if ($null -ne $base -and $base -gt 0) {
        $regressionGate = [Math]::Round([double]$base * 1.5, 4)
    }

    # Hard gate: ADR-008 ceiling (always present).
    $hardGate = if ($null -ne $hardCap) { [double]$hardCap } else { $null }

    $regressionPass = ($null -eq $regressionGate) -or ($actual -le $regressionGate)
    $hardPass       = ($null -eq $hardGate)       -or ($actual -le $hardGate)
    $pass           = $regressionPass -and $hardPass

    if (-not $pass) { $failed = $true }

    $results += [pscustomobject]@{
        metric         = $metric
        actual         = $actual
        baseline       = $base
        regressionGate = $regressionGate
        hardCap        = $hardGate
        regressionPass = $regressionPass
        hardPass       = $hardPass
        pass           = $pass
    }
}

# ── Pretty print + JSON summary ─────────────────────────────────────────────
Write-Host ""
Write-Host "ADR-008 perf gate results" -ForegroundColor Yellow
Write-Host ("─" * 60)
$results | Format-Table metric, actual, baseline, regressionGate, hardCap, pass -AutoSize | Out-Host

$summary = [ordered]@{
    timestamp        = (Get-Date).ToUniversalTime().ToString('o')
    baselineCommit   = $baseline.lastUpdatedCommit
    pass             = -not $failed
    metrics          = $results
}
$summaryJson = $summary | ConvertTo-Json -Depth 6 -Compress
Write-Host ""
Write-Host "==> JSON summary"
Write-Host $summaryJson

if ($failed) {
    Write-Host ""
    Write-Host "FAIL — at least one ADR-008 metric regressed or exceeded its hard ceiling." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "PASS — all gated metrics within budget." -ForegroundColor Green
exit 0
