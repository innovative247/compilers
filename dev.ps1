# dev.ps1 — Bump patch version, publish win-x64, deploy to bin\win-x64 for local testing
# Usage: .\dev.ps1
#
# Increments the patch number automatically (2.0.38 -> 2.0.39).
# Only builds win-x64 — fast iteration, no archives created.
# Run .\release.ps1 -Version X.Y.Z -Notes "..." when ready to ship.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Kill any running compiler processes that would lock the DLLs
$compilerProcesses = @('runsql','isqlline','set_profile','iwho','iwatch','iplan','iplanext',
    'runcreate','eact','set_actions','eloc','set_table_locations','eopt','set_options',
    'compile_msg','set_messages','compile_required_fields','set_required_fields',
    'i_run_upgrade','transfer_data','bcp_data','extract_msg')
$killed = @()
foreach ($name in $compilerProcesses) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        $killed += $name
    }
}
if ($killed.Count -gt 0) {
    Write-Host "  Stopped: $($killed -join ', ')" -ForegroundColor Yellow
}

$projects = @(
    'runsql'
    'isqlline'
    'set_profile'
    'iwho'
    'iwatch'
    'iplan'
    'iplanext'
    'runcreate'
    'eact'
    'set_actions'
    'eloc'
    'set_table_locations'
    'eopt'
    'set_options'
    'compile_msg'
    'set_messages'
    'compile_required_fields'
    'set_required_fields'
    'i_run_upgrade'
    'transfer_data'
    'bcp_data'
    'extract_msg'
)

# --- Bump patch version ---
$propsFile = Join-Path $PSScriptRoot "src\Directory.Build.props"
$content   = Get-Content $propsFile -Raw

if ($content -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
    Write-Host "ERROR: Could not read version from Directory.Build.props" -ForegroundColor Red
    exit 1
}
$major   = [int]$Matches[1]
$minor   = [int]$Matches[2]
$patch   = [int]$Matches[3] + 1
$version = "$major.$minor.$patch"

$content = $content -replace '<Version>[^<]+</Version>',         "<Version>$version</Version>"
$content = $content -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$version.0</AssemblyVersion>"
$content = $content -replace '<FileVersion>[^<]+</FileVersion>',         "<FileVersion>$version.0</FileVersion>"
Set-Content $propsFile $content -NoNewline

Write-Host "=== dev build v$version ===" -ForegroundColor Cyan
Write-Host ""

# --- Preserve settings.json ---
$outputDir    = Join-Path $PSScriptRoot "bin\win-x64"
$settingsFile = Join-Path $outputDir "settings.json"
$settingsBackup = $null
if (Test-Path $settingsFile) {
    $settingsBackup = Get-Content $settingsFile -Raw
}

# --- Publish win-x64 only ---
$failed = @()
foreach ($project in $projects) {
    $csproj = Join-Path $PSScriptRoot "src\$project\$project.csproj"
    Write-Host "  $project..." -NoNewline

    dotnet publish $csproj -c Release -r win-x64 --self-contained `
        -o $outputDir --nologo -v quiet 2>&1 | Out-Null

    if ($LASTEXITCODE -eq 0) {
        Write-Host " OK" -ForegroundColor Green
    } else {
        Write-Host " FAILED" -ForegroundColor Red
        $failed += $project
    }
}

# --- Restore settings.json ---
if ($settingsBackup) {
    Set-Content $settingsFile $settingsBackup -NoNewline
}

Write-Host ""

if ($failed.Count -gt 0) {
    Write-Host "Failed:" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "v$version deployed to bin\win-x64" -ForegroundColor Green
Write-Host "Run 'runsql version' to confirm." -ForegroundColor DarkGray
