<#
.SYNOPSIS
ADR-008 host-side BDN regression gate. Reads
BenchmarkDotNet.Artifacts/results/AgentWorkspace.Benchmarks.Mvp8.*-report.csv,
extracts the Mean for each gated method, normalises units, and compares to
1.5× the saved baseline values in baseline.json. Either gate failing fails
the script (exit 1).

.DESCRIPTION
Maintenance slot — closes "BDN nightly cron" from ADR-016. The four MVP-8
host-side benches (PtyReadWrite, BurstRender, PolicyEval, RedactionEval)
take ~5 min on ShortRun and are too long for the per-PR perf-gate.yml; this
script gates them on the bdn-nightly.yml schedule instead.

.PARAMETER ResultsDir
BenchmarkDotNet artifact directory. Defaults to ./BenchmarkDotNet.Artifacts/results.

.PARAMETER BaselinePath
Path to baseline.json. Defaults to src/AgentWorkspace.Benchmarks/Mvp8/baseline.json.

.OUTPUTS
Single-line JSON summary on stdout (last line). Exit 0 = all pass, 1 = at
least one regression.
#>

[CmdletBinding()]
param(
    [string] $ResultsDir   = (Join-Path $PSScriptRoot '..' 'BenchmarkDotNet.Artifacts/results'),
    [string] $BaselinePath = (Join-Path $PSScriptRoot '..' 'src/AgentWorkspace.Benchmarks/Mvp8/baseline.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path $ResultsDir))   { throw "BDN results dir not found at $ResultsDir" }
if (-not (Test-Path $BaselinePath)) { throw "baseline.json not found at $BaselinePath" }

$baseline = Get-Content $BaselinePath -Raw | ConvertFrom-Json

# BDN Mean column → nanoseconds. Handles "3,935.2 ns", "1.5 ms", "5.4 us",
# "12.3 µs" (U+00B5), and "16.8 μs" (U+03BC — BDN's actual choice).
function ConvertTo-Nanoseconds {
    param([string] $value)
    $clean = $value -replace '[^\d.A-Za-zµμ]', ''
    if ($clean -match '^([0-9.]+)(ns|us|µs|μs|ms|s)$') {
        $n = [double] $Matches[1]
        switch -Regex ($Matches[2]) {
            'ns'        { return $n }
            '^(us|µs|μs)$' { return $n * 1e3 }
            'ms'        { return $n * 1e6 }
            's'         { return $n * 1e9 }
        }
    }
    throw "Unparseable BDN mean value: '$value'"
}

# Find the row whose Method column matches $methodPattern. Prefer the non-ShortRun job
# (full --job default), fall back to ShortRun if that's all that ran.
function Get-BdnMean {
    param(
        [string] $CsvPath,
        [string] $MethodPattern
    )
    if (-not (Test-Path $CsvPath)) { return $null }
    $rows = Import-Csv $CsvPath
    $matches = $rows | Where-Object { $_.Method -like $MethodPattern }
    if (-not $matches) { return $null }

    $primary = $matches | Where-Object { $_.Job -notlike '*Short*' } | Select-Object -First 1
    $row     = if ($primary) { $primary } else { $matches | Select-Object -First 1 }
    return @{
        meanNs = ConvertTo-Nanoseconds $row.Mean
        job    = $row.Job
    }
}

# Method → baseline metric mapping. Same shape used by the day-59 measurement
# that established each baseline number.
$gateMap = @(
    @{
        # Description is "PTY read→write cycle  64 KB (ADR-008 #2)" (Day 55).
        csv          = 'AgentWorkspace.Benchmarks.Mvp8.PtyReadWriteBench-report.csv'
        methodLike   = '*64 KB*'
        baselineKey  = 'ptyReadWriteP95Ms'
        baselineUnit = 'ms'
    }
    @{
        csv          = 'AgentWorkspace.Benchmarks.Mvp8.BurstRenderBench-report.csv'
        methodLike   = '*128* 8KB*'
        baselineKey  = 'burstRender1MbMs'
        baselineUnit = 'ms'
    }
    @{
        csv          = 'AgentWorkspace.Benchmarks.Mvp8.PolicyEvalBench-report.csv'
        methodLike   = '*50-rule miss*'
        baselineKey  = 'policyEval50RuleNs'
        baselineUnit = 'ns'
    }
    @{
        csv          = 'AgentWorkspace.Benchmarks.Mvp8.RedactionEvalBench-report.csv'
        methodLike   = '*1KB bulk*'
        baselineKey  = 'redactionEval14RuleNs'
        baselineUnit = 'ns'
    }
)

$results = @()
$failed  = $false

foreach ($g in $gateMap) {
    $csvPath  = Join-Path $ResultsDir $g.csv
    $reading  = Get-BdnMean -CsvPath $csvPath -MethodPattern $g.methodLike
    $baseVal  = $baseline.metrics.($g.baselineKey)

    if ($null -eq $reading) {
        $results += [pscustomobject]@{
            metric       = $g.baselineKey
            actual       = $null
            baseline     = $baseVal
            regression   = $null
            pass         = $false
            note         = "No matching row in $($g.csv) for pattern '$($g.methodLike)'"
        }
        $failed = $true
        continue
    }

    # Convert measured value (ns) to baseline unit.
    $actualInBaseUnit = switch ($g.baselineUnit) {
        'ns' { $reading.meanNs }
        'us' { $reading.meanNs / 1e3 }
        'ms' { $reading.meanNs / 1e6 }
        default { throw "Unknown baseline unit '$($g.baselineUnit)'" }
    }
    $actualRounded = [Math]::Round($actualInBaseUnit, 4)

    $regressionGate = $null
    $pass = $true
    if ($null -ne $baseVal -and $baseVal -gt 0) {
        $regressionGate = [Math]::Round([double]$baseVal * 1.5, 4)
        $pass = $actualRounded -le $regressionGate
    }

    if (-not $pass) { $failed = $true }

    $results += [pscustomobject]@{
        metric         = $g.baselineKey
        actual         = $actualRounded
        baseline       = $baseVal
        regressionGate = $regressionGate
        unit           = $g.baselineUnit
        bdnJob         = $reading.job
        pass           = $pass
    }
}

Write-Host ""
Write-Host "BDN baseline regression gate" -ForegroundColor Yellow
Write-Host ("─" * 60)
$results | Format-Table metric, actual, baseline, regressionGate, unit, bdnJob, pass -AutoSize | Out-Host

$summary = [ordered]@{
    timestamp      = (Get-Date).ToUniversalTime().ToString('o')
    baselineCommit = $baseline.lastUpdatedCommit
    pass           = -not $failed
    metrics        = $results
}
$summaryJson = $summary | ConvertTo-Json -Depth 6 -Compress
Write-Host ""
Write-Host "==> JSON summary"
Write-Host $summaryJson

if ($failed) {
    Write-Host ""
    Write-Host "FAIL — at least one host-side BDN metric regressed past 1.5× baseline." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "PASS — all gated BDN metrics within 1.5× baseline." -ForegroundColor Green
exit 0
