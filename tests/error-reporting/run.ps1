# run.ps1 -- error-reporting corpus runner (Windows / Linux / macOS, PowerShell 7+)
#
# Every case in cases.tsv is run TWICE per platform: once as inline SQL through
# isqlline, once as a script file through runsql. Both must agree with cases.tsv.
#
# Usage:
#   ./run.ps1 -Sybase GONZO:sbnpro
#   ./run.ps1 -Sybase GONZO:sbnpro -Mssql SRM_LOCAL:master -Postgres PGTEST:pgtest
#   ./run.ps1 -Sybase GONZO:sbnpro -Bin ../../bin/win-x64
#
# Omit a platform to skip it. Exit code is 0 only when every case passed.
# The sh runner (run.sh) reads the same cases.tsv and asserts the same things.

[CmdletBinding()]
param(
    [string]$Sybase,
    [string]$Mssql,
    [string]$Postgres,
    [string]$Bin
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath

# Binaries: -Bin wins, else whatever is on PATH.
$exeSuffix = if ($IsWindows -or $null -eq $IsWindows) { '.exe' } else { '' }
function Resolve-Tool([string]$name) {
    if ($Bin) { return (Join-Path $Bin "$name$exeSuffix") }
    return $name
}
$isqlline = Resolve-Tool 'isqlline'
$runsql   = Resolve-Tool 'runsql'

$script:Pass = 0
$script:Fail = 0
$script:Skip = 0
$script:Failures = @()

function Invoke-Capture {
    # NB: the parameter must NOT be called $Args -- that is a PowerShell automatic
    # variable and binding it here silently swallows every argument.
    param([string]$Exe, [string[]]$CliArgs)
    # Capture stdout+stderr together; the compilers write errors to both depending on command.
    $out = & $Exe @CliArgs 2>&1 | Out-String
    return [pscustomobject]@{ Output = $out; ExitCode = $LASTEXITCODE }
}

function Test-Outcome {
    param([string]$Label, [string]$Expect, $Result)

    $out = $Result.Output
    if ($Expect -eq 'ok') {
        if ($Result.ExitCode -ne 0) {
            return "$Label - expected success, got exit $($Result.ExitCode). output:`n$out"
        }
        if ($out -match '(?m)^\s*Msg ') {
            return "$Label - expected success but an error was reported. output:`n$out"
        }
        return $null
    }

    $needle = $Expect.Substring('error:'.Length)
    if ($Result.ExitCode -eq 0) {
        return "$Label - expected '$needle' and a non-zero exit, got exit 0. output:`n$out"
    }
    $hits = ([regex]::Matches($out, [regex]::Escape($needle))).Count
    if ($hits -eq 0) {
        return "$Label - expected '$needle' in the output. output:`n$out"
    }
    if ($hits -gt 1) {
        return "$Label - '$needle' reported $hits times; the error is being printed more than once. output:`n$out"
    }
    return $null
}

# Inline form of a case file: drop the batch terminator, collapse to one line.
function Get-InlineSql([string]$path) {
    $lines = Get-Content $path | Where-Object { $_.Trim() -ne '' -and $_.Trim() -notmatch '^(?i)go$' }
    return ($lines -join ' ').Trim()
}

function Invoke-Platform {
    param([string]$Name, [string]$Target, [int]$Column)

    if (-not $Target) { return }
    $parts = $Target.Split(':', 2)
    if ($parts.Count -ne 2 -or -not $parts[0] -or -not $parts[1]) {
        throw "-$Name expects PROFILE:DATABASE (e.g. GONZO:sbnpro), got '$Target'"
    }
    $profileName = $parts[0]
    $database    = $parts[1]

    Write-Host ""
    Write-Host "--- $Name ($profileName.$database) ---" -ForegroundColor Cyan

    foreach ($row in $script:Cases) {
        $expect = $row[$Column]
        $file   = Join-Path (Join-Path $root 'cases') $row[1]
        $id     = $row[0]

        if ($expect -eq 'n/a') {
            Write-Host "[SKIP]  $id" -ForegroundColor DarkGray
            $script:Skip++
            continue
        }

        $problems = @()

        $inline = Get-InlineSql $file
        $r = Invoke-Capture $isqlline @($inline, $database, $profileName)
        $p = Test-Outcome "$id/isqlline" $expect $r
        if ($p) { $problems += $p }

        $r = Invoke-Capture $runsql @($file, $database, $profileName, '--changelog:n')
        $p = Test-Outcome "$id/runsql" $expect $r
        if ($p) { $problems += $p }

        if ($problems.Count -eq 0) {
            Write-Host "[PASS]  $id" -ForegroundColor Green
            $script:Pass++
        } else {
            Write-Host "[FAIL]  $id" -ForegroundColor Red
            $script:Fail++
            $script:Failures += $problems
        }
    }
}

# --- Load cases.tsv ---
$script:Cases = @()
foreach ($line in (Get-Content (Join-Path $root 'cases.tsv'))) {
    if ($line -match '^\s*#' -or $line.Trim() -eq '') { continue }
    $cols = $line -split "`t"
    if ($cols[0] -eq 'id') { continue }   # header
    if ($cols.Count -lt 5) { throw "malformed cases.tsv row: $line" }
    $script:Cases += ,$cols
}

Write-Host "=== Compilers error-reporting corpus ===" -ForegroundColor Cyan
Write-Host "Cases    : $($script:Cases.Count)"
Write-Host "Binaries : $(if ($Bin) { $Bin } else { 'PATH' })"

if (-not $Sybase -and -not $Mssql -and -not $Postgres) {
    Write-Host ""
    Write-Host "Nothing to do: pass at least one of -Sybase / -Mssql / -Postgres as PROFILE:DATABASE." -ForegroundColor Yellow
    exit 2
}

Invoke-Platform 'Sybase'   $Sybase   2
Invoke-Platform 'Mssql'    $Mssql    3
Invoke-Platform 'Postgres' $Postgres 4

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
if ($script:Fail -gt 0) { Write-Host "FAIL  $($script:Fail)" -ForegroundColor Red }
Write-Host "PASS  $($script:Pass)" -ForegroundColor Green
if ($script:Skip -gt 0) { Write-Host "SKIP  $($script:Skip)" -ForegroundColor DarkGray }

if ($script:Fail -gt 0) {
    Write-Host ""
    Write-Host "Failures:" -ForegroundColor Red
    $script:Failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
exit 0
