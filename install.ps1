# install.ps1 — IBS Compilers installer for Windows
# One-liner: irm https://raw.githubusercontent.com/innovative247/compilers/main/compilers-net8/install.ps1 | iex

$ErrorActionPreference = 'Stop'

$repo = "innovative247/compilers"
$assetName = "compilers-net8-win-x64.zip"
$installDir = Join-Path $env:LOCALAPPDATA "ibs-compilers"

Write-Host "IBS Compilers Installer" -ForegroundColor Cyan
Write-Host ""

# --- Detect and remove existing Python installation ---
$pythonInstalled = $false
$pipInfo = $null

try {
    $pipInfo = & python -m pip show ibs_compilers 2>$null
    if ($LASTEXITCODE -eq 0 -and $pipInfo) {
        $pythonInstalled = $true
    }
} catch {}

# Also check for Python entry points in common Scripts dirs
$pythonScriptsDirs = @()
if (-not $pythonInstalled) {
    $candidates = @(
        (Join-Path $env:APPDATA "Python\Python*\Scripts"),
        (Join-Path $env:LOCALAPPDATA "Programs\Python\Python*\Scripts")
    )
    foreach ($pattern in $candidates) {
        $dirs = Get-Item $pattern -ErrorAction SilentlyContinue
        foreach ($d in $dirs) {
            if (Test-Path (Join-Path $d.FullName "runsql.exe")) {
                $pythonScriptsDirs += $d.FullName
                $pythonInstalled = $true
            }
        }
    }
}

if ($pythonInstalled) {
    Write-Host "=== Existing Python installation detected ===" -ForegroundColor Yellow
    Write-Host ""

    $pythonSettings = $null

    if ($pipInfo) {
        $pipVersion = ($pipInfo | Select-String "^Version:") -replace "^Version:\s*", ""
        $pipLocation = ($pipInfo | Select-String "^Location:") -replace "^Location:\s*", ""
        $pipEditable = ($pipInfo | Select-String "^Editable project location:") -replace "^Editable project location:\s*", ""
        Write-Host "  Python package: ibs_compilers v$pipVersion" -ForegroundColor White
        if ($pipLocation) { Write-Host "  Location:       $pipLocation" -ForegroundColor Gray }

        # Find existing settings.json from Python installation
        # Try 1: Ask Python directly
        try {
            $pythonSettings = & python -c "from commands.ibs_common import find_settings_file; f = find_settings_file(); print(f) if f.exists() else None" 2>$null
            if ($pythonSettings -and -not (Test-Path $pythonSettings)) { $pythonSettings = $null }
        } catch { $pythonSettings = $null }

        # Try 2: Editable project location (settings.json at project root)
        if (-not $pythonSettings -and $pipEditable -and (Test-Path (Join-Path $pipEditable "settings.json"))) {
            $pythonSettings = Join-Path $pipEditable "settings.json"
        }

        # Try 3: Location field — for editable installs, go one level up from site-packages
        if (-not $pythonSettings -and $pipLocation) {
            $parentDir = Split-Path $pipLocation -Parent
            if ($parentDir -and (Test-Path (Join-Path $parentDir "settings.json"))) {
                $pythonSettings = Join-Path $parentDir "settings.json"
            }
        }

        if ($pythonSettings) {
            Write-Host "  Settings:       $pythonSettings" -ForegroundColor Gray
        }
    }

    if ($pythonScriptsDirs.Count -gt 0) {
        foreach ($d in $pythonScriptsDirs) {
            Write-Host "  Scripts dir:    $d" -ForegroundColor Gray
        }
    }

    Write-Host ""
    Write-Host "  The .NET 8 version replaces the Python version." -ForegroundColor White
    Write-Host "  The Python compilers must be removed to avoid conflicts." -ForegroundColor White
    Write-Host ""
    $removePython = Read-Host "  Remove Python installation? [Y/n]"
    if (-not $removePython) { $removePython = "Y" }

    if ($removePython.ToLower() -ne "n" -and $removePython.ToLower() -ne "no") {
        Write-Host ""

        # Step 1: Uninstall pip package
        if ($pipInfo) {
            Write-Host "  Uninstalling pip package ibs_compilers..."
            try {
                & python -m pip uninstall ibs_compilers -y 2>$null | Out-Null
            } catch {}
        }

        # Step 2: Remove leftover entry points from Python Scripts dirs
        $pythonCommands = @(
            'runsql', 'isqlline', 'set_profile', 'set_actions', 'eact', 'compile_actions',
            'set_table_locations', 'eloc', 'create_tbl_locations', 'set_options', 'eopt',
            'import_options', 'set_messages', 'compile_msg', 'install_msg', 'extract_msg',
            'set_required_fields', 'ereq', 'install_required_fields', 'i_run_upgrade',
            'runcreate', 'transfer_data', 'iplan', 'iplanext', 'iwho'
        )

        foreach ($d in $pythonScriptsDirs) {
            $removed = 0
            foreach ($cmd in $pythonCommands) {
                $exe = Join-Path $d "$cmd.exe"
                $script = Join-Path $d "$cmd-script.py"
                if (Test-Path $exe) { Remove-Item $exe -Force; $removed++ }
                if (Test-Path $script) { Remove-Item $script -Force }
            }
            if ($removed -gt 0) {
                Write-Host "  Removed $removed Python entry points from $d" -ForegroundColor Gray
            }
        }

        # Step 3: Clean Python Scripts dir from user PATH if now empty of our commands
        $userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
        if ($userPath) {
            $cleanedDirs = @()
            $pathChanged = $false
            foreach ($entry in $userPath.Split(';')) {
                $trimmed = $entry.Trim()
                if (-not $trimmed) { continue }
                $isPythonScripts = $false
                foreach ($d in $pythonScriptsDirs) {
                    if ($trimmed -eq $d) { $isPythonScripts = $true; break }
                }
                # Only remove if it was a Python Scripts dir AND has no other commands left
                if ($isPythonScripts) {
                    $hasOtherExes = (Get-ChildItem -Path $trimmed -Filter "*.exe" -ErrorAction SilentlyContinue | Measure-Object).Count -gt 0
                    if (-not $hasOtherExes) {
                        Write-Host "  Removed empty Python Scripts dir from PATH: $trimmed" -ForegroundColor Gray
                        $pathChanged = $true
                        continue
                    }
                }
                $cleanedDirs += $trimmed
            }
            if ($pathChanged) {
                [Environment]::SetEnvironmentVariable("PATH", ($cleanedDirs -join ";"), "User")
            }
        }

        Write-Host ""
        Write-Host "  Python installation removed." -ForegroundColor Green
        Write-Host ""
    } else {
        Write-Host ""
        Write-Host "  WARNING: Python compilers left in place. Commands may shadow" -ForegroundColor Yellow
        Write-Host "  the .NET 8 versions depending on your PATH order." -ForegroundColor Yellow
        Write-Host ""
    }
}

# --- Install .NET 8 compilers ---

# Get latest release
Write-Host "Checking latest release..."
$releaseUrl = "https://api.github.com/repos/$repo/releases/latest"
$headers = @{ "User-Agent" = "IBS-Compilers-Installer" }

try {
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers
} catch {
    Write-Host "Failed to fetch release info. Check your internet connection." -ForegroundColor Red
    exit 1
}

$version = $release.tag_name
Write-Host "Latest version: $version" -ForegroundColor Green

# Find download URL
$asset = $release.assets | Where-Object { $_.name -eq $assetName }
if (-not $asset) {
    Write-Host "Could not find $assetName in release assets." -ForegroundColor Red
    exit 1
}

$downloadUrl = $asset.browser_download_url

# Download
$tempFile = Join-Path $env:TEMP "compilers-net8.zip"
Write-Host "Downloading $assetName..."
Invoke-WebRequest -Uri $downloadUrl -OutFile $tempFile -Headers $headers -UseBasicParsing

# Extract
Write-Host "Installing to $installDir..."
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

Expand-Archive -Path $tempFile -DestinationPath $installDir -Force
Remove-Item $tempFile -ErrorAction SilentlyContinue

# Migrate or create settings.json
$settingsExample = Join-Path $installDir "settings.json.example"
$settingsFile = Join-Path $installDir "settings.json"
if (-not (Test-Path $settingsFile)) {
    if ($pythonSettings -and (Test-Path $pythonSettings)) {
        Write-Host ""
        Write-Host "  Found existing settings.json with your profiles:" -ForegroundColor White
        Write-Host "    $pythonSettings" -ForegroundColor Gray
        $migrateSettings = Read-Host "  Copy to new installation? [Y/n]"
        if (-not $migrateSettings) { $migrateSettings = "Y" }
        if ($migrateSettings.ToLower() -ne "n" -and $migrateSettings.ToLower() -ne "no") {
            Copy-Item $pythonSettings $settingsFile
            Write-Host "  Migrated settings.json with your existing profiles." -ForegroundColor Green
        } elseif (Test-Path $settingsExample) {
            Copy-Item $settingsExample $settingsFile
            Write-Host "Created settings.json from example." -ForegroundColor Yellow
        }
    } elseif (Test-Path $settingsExample) {
        Copy-Item $settingsExample $settingsFile
        Write-Host "Created settings.json from example." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Installed $version to $installDir" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Run: $installDir\set_profile.exe configure" -ForegroundColor White
Write-Host "     (adds compilers to your PATH and verifies setup)" -ForegroundColor Gray
Write-Host "  2. Restart your terminal" -ForegroundColor White
Write-Host "  3. Run: set_profile" -ForegroundColor White
Write-Host "     (configure database connections)" -ForegroundColor Gray
