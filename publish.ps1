# publish.ps1 — Publish all .NET 8 compiler executables for all platforms
# Usage: .\publish.ps1 [-Version 1.0.1]
# All projects are published self-contained to a shared bin/<platform>/ directory.
# The .NET runtime is output once; each command adds only its small exe/dll.
# After publishing, creates distributable archives in bin/.

param(
    [string]$Version = ""
)

$ErrorActionPreference = 'Stop'

$runtimes = @('win-x64', 'linux-x64', 'osx-x64')

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

# If version specified, update Directory.Build.props
if ($Version) {
    $propsFile = Join-Path $PSScriptRoot "src\Directory.Build.props"
    if (Test-Path $propsFile) {
        $content = Get-Content $propsFile -Raw
        $content = $content -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
        $content = $content -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
        $content = $content -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version.0</FileVersion>"
        Set-Content $propsFile $content -NoNewline
        Write-Host "Version set to $Version" -ForegroundColor Yellow
    }
}

$allFailed = @()

foreach ($rid in $runtimes) {
    $outputDir = Join-Path $PSScriptRoot "bin\$rid"

    # Clean previous output for this platform (preserve settings.json)
    if (Test-Path $outputDir) {
        $settingsBackup = $null
        $settingsFile = Join-Path $outputDir "settings.json"
        if (Test-Path $settingsFile) {
            $settingsBackup = Get-Content $settingsFile -Raw
        }
        Remove-Item $outputDir -Recurse -Force
        if ($settingsBackup) {
            New-Item $outputDir -ItemType Directory -Force | Out-Null
            Set-Content (Join-Path $outputDir "settings.json") $settingsBackup -NoNewline
            Write-Host "  Preserved settings.json" -ForegroundColor Yellow
        }
    }

    Write-Host "=== Publishing $($projects.Count) projects for $rid ===" -ForegroundColor Cyan
    Write-Host ""

    foreach ($project in $projects) {
        $csproj = Join-Path $PSScriptRoot "src\$project\$project.csproj"
        Write-Host "  Publishing $project..." -NoNewline

        dotnet publish $csproj -c Release -r $rid --self-contained `
            -o $outputDir --nologo -v quiet 2>&1 | Out-Null

        if ($LASTEXITCODE -eq 0) {
            Write-Host " OK" -ForegroundColor Green
        } else {
            Write-Host " FAILED" -ForegroundColor Red
            $allFailed += "$rid/$project"
        }
    }

    Write-Host ""
}

# Summary
Write-Host "=== Summary ===" -ForegroundColor Cyan

foreach ($rid in $runtimes) {
    $outputDir = Join-Path $PSScriptRoot "bin\$rid"
    if (Test-Path $outputDir) {
        if ($rid -eq 'win-x64') {
            $count = (Get-ChildItem -Path $outputDir -Filter '*.exe' | Measure-Object).Count
        } else {
            # Linux/macOS: count executables (files with no extension matching project names)
            $count = ($projects | Where-Object { Test-Path (Join-Path $outputDir $_) } | Measure-Object).Count
        }
        $totalSize = (Get-ChildItem -Path $outputDir -Recurse | Measure-Object -Property Length -Sum).Sum
        $sizeMB = [math]::Round($totalSize / 1MB, 1)
        Write-Host "  $rid`: $count executables, $sizeMB MB total" -ForegroundColor Green
    } else {
        Write-Host "  $rid`: output directory missing" -ForegroundColor Red
    }
}

Write-Host ""

if ($allFailed.Count -gt 0) {
    Write-Host "Failed:" -ForegroundColor Red
    $allFailed | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
} else {
    Write-Host "All platforms published successfully." -ForegroundColor Green
}

# Create distributable archives
Write-Host ""
Write-Host "=== Creating archives ===" -ForegroundColor Cyan

$binDir = Join-Path $PSScriptRoot "bin"

# Copy settings.json.example and readme.md into each platform dir
$settingsExample = Join-Path $PSScriptRoot "settings.json.example"
$readmeFile = Join-Path $PSScriptRoot "readme.md"
foreach ($rid in $runtimes) {
    $destDir = Join-Path $binDir $rid
    if (Test-Path $destDir) {
        if (Test-Path $settingsExample) { Copy-Item $settingsExample (Join-Path $destDir "settings.json.example") }
        if (Test-Path $readmeFile)      { Copy-Item $readmeFile      (Join-Path $destDir "readme.md") }
    }
}

# Windows zip (exclude settings.json — never overwrite user credentials on update)
$winDir = Join-Path $binDir "win-x64"
if (Test-Path $winDir) {
    $zipPath = Join-Path $binDir "compilers-net8-win-x64.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $filesToZip = Get-ChildItem -Path $winDir | Where-Object { $_.Name -ne "settings.json" }
    Compress-Archive -Path ($filesToZip.FullName) -DestinationPath $zipPath
    $sizeMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "  Created: compilers-net8-win-x64.zip ($sizeMB MB)" -ForegroundColor Green
}

# Linux tar.gz (exclude settings.json)
$linuxDir = Join-Path $binDir "linux-x64"
if (Test-Path $linuxDir) {
    $tarPath = Join-Path $binDir "compilers-net8-linux-x64.tar.gz"
    if (Test-Path $tarPath) { Remove-Item $tarPath }
    tar -czf $tarPath --exclude='./settings.json' -C $linuxDir .
    if (Test-Path $tarPath) {
        $sizeMB = [math]::Round((Get-Item $tarPath).Length / 1MB, 1)
        Write-Host "  Created: compilers-net8-linux-x64.tar.gz ($sizeMB MB)" -ForegroundColor Green
    } else {
        Write-Host "  Warning: compilers-net8-linux-x64.tar.gz not created (known Windows tar issue; run manually)" -ForegroundColor Yellow
    }
}

# macOS tar.gz (exclude settings.json)
$osxDir = Join-Path $binDir "osx-x64"
if (Test-Path $osxDir) {
    $tarPath = Join-Path $binDir "compilers-net8-osx-x64.tar.gz"
    if (Test-Path $tarPath) { Remove-Item $tarPath }
    tar -czf $tarPath --exclude='./settings.json' -C $osxDir .
    if (Test-Path $tarPath) {
        $sizeMB = [math]::Round((Get-Item $tarPath).Length / 1MB, 1)
        Write-Host "  Created: compilers-net8-osx-x64.tar.gz ($sizeMB MB)" -ForegroundColor Green
    } else {
        Write-Host "  Warning: compilers-net8-osx-x64.tar.gz not created (known Windows tar issue; run manually)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
