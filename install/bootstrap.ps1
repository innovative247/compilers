<#
.SYNOPSIS
    IBS Compilers Bootstrap Script for Windows

.DESCRIPTION
    Lightweight bootstrap script that ensures Python is installed,
    then hands off to installer_windows.py for platform-specific installation.

    This script only handles:
    - Python version verification
    - Python installation via winget (if needed)
    - Launching installer_windows.py

.NOTES
    Run from PowerShell (Admin recommended for PATH modifications)
    Usage: .\bootstrap.ps1

.EXAMPLE
    .\bootstrap.ps1
    .\bootstrap.ps1 -SkipPythonInstall
    .\bootstrap.ps1 -Help
#>

param(
    [switch]$SkipPythonInstall,
    [switch]$Help
)

# =============================================================================
# CONFIGURATION
# =============================================================================

$Script:InstallDir = $PSScriptRoot
$Script:ProjectRoot = Split-Path $Script:InstallDir -Parent
$Script:InstallerScript = Join-Path $Script:InstallDir "installer_windows.py"
$Script:RequiredPythonVersion = [Version]"3.8"
$Script:LogFile = Join-Path $Script:InstallDir "installer.log"

# =============================================================================
# HELPER FUNCTIONS
# =============================================================================

function Initialize-LogFile {
    <#
    .SYNOPSIS
        Initialize the log file with header information.
    .DESCRIPTION
        Creates/overwrites installer.log with platform and Python version info.
        Matches the format used by installer_windows.py.
    #>

    # Get Windows version
    $osVersion = [System.Environment]::OSVersion.Version
    $osName = "Windows $($osVersion.Major).$($osVersion.Minor)"

    # Get Python version (if available)
    $pythonVersion = "Not detected yet"
    $pythonInfo = Get-PythonInfo
    if ($null -ne $pythonInfo) {
        $pythonVersion = $pythonInfo.VersionString
    }

    $header = @"
IBS Compilers Installer Log
Platform: $osName
Python: $pythonVersion
============================================================

"@

    try {
        # Overwrite log file with header
        Set-Content -Path $Script:LogFile -Value $header -Encoding UTF8 -Force
    } catch {
        Write-Warning "Could not initialize log file: $_"
    }
}

function Write-Status {
    <#
    .SYNOPSIS
        Write a status message to console and log file.
    .DESCRIPTION
        Displays a formatted message with color and prefix, and appends to installer.log.
        Matches the logging format used by installer_windows.py.
    #>
    param(
        [string]$Message,
        [ValidateSet("Info", "Warn", "Error", "Success", "Step")]
        [string]$Level = "Info"
    )

    $color = switch ($Level) {
        "Info"    { "White" }
        "Warn"    { "Yellow" }
        "Error"   { "Red" }
        "Success" { "Green" }
        "Step"    { "Cyan" }
    }

    $prefix = switch ($Level) {
        "Info"    { "   " }
        "Warn"    { " ! " }
        "Error"   { " X " }
        "Success" { " + " }
        "Step"    { ">> " }
    }

    # Map to log file levels (uppercase)
    $logLevel = switch ($Level) {
        "Info"    { "INFO" }
        "Warn"    { "WARN" }
        "Error"   { "ERROR" }
        "Success" { "SUCCESS" }
        "Step"    { "STEP" }
    }

    # Write to console
    Write-Host "$prefix$Message" -ForegroundColor $color

    # Write to log file
    try {
        $logEntry = "[$logLevel] $Message"
        Add-Content -Path $Script:LogFile -Value $logEntry -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch {
        # Silently ignore log file write errors (log file may not exist yet)
    }
}

function Get-PythonInfo {
    <#
    .SYNOPSIS
        Find Python and return its command and version.
    #>

    $pythonCommands = @("python3", "python", "py")

    foreach ($cmd in $pythonCommands) {
        $cmdPath = Get-Command $cmd -ErrorAction SilentlyContinue
        if ($null -eq $cmdPath) { continue }

        try {
            if ($cmd -eq "py") {
                $versionOutput = & py -3 --version 2>&1
                $actualCmd = "py -3"
            } else {
                $versionOutput = & $cmd --version 2>&1
                $actualCmd = $cmd
            }

            if ($versionOutput -match "Python (\d+\.\d+\.\d+)") {
                return @{
                    Command = $actualCmd
                    Version = [Version]$Matches[1]
                    VersionString = $versionOutput.Trim()
                    Path = $cmdPath.Source
                }
            }
        } catch {
            continue
        }
    }

    return $null
}

function Test-PythonVersion {
    param([Version]$Version)
    return $Version -ge $Script:RequiredPythonVersion
}

function Install-PythonViaWinget {
    <#
    .SYNOPSIS
        Install Python using winget.
    #>

    Write-Status "Checking for winget package manager" "Info"
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        Write-Status "winget not found - cannot auto-install Python" "Error"
        Write-Host ""
        Write-Host "  Please install Python manually:" -ForegroundColor White
        Write-Host "  1. Download from: https://www.python.org/downloads/" -ForegroundColor Yellow
        Write-Host "  2. Run the installer" -ForegroundColor Yellow
        Write-Host "  3. CHECK 'Add Python to PATH' during installation!" -ForegroundColor Red
        Write-Host "  4. Re-run this bootstrap script" -ForegroundColor Yellow
        Write-Host ""
        return $false
    }

    Write-Status "winget found at: $($winget.Source)" "Success"
    Write-Status "Installing Python 3.11 via winget..." "Step"
    Write-Host ""
    Write-Host "  This will download and install Python." -ForegroundColor White
    Write-Host "  Please wait for the installation to complete." -ForegroundColor White
    Write-Host ""

    try {
        Write-Status "Running: winget install -e --id Python.Python.3.11" "Info"
        $null = winget install -e --id Python.Python.3.11 --accept-source-agreements --accept-package-agreements
        Write-Status "Python installation completed" "Success"
        return $true
    } catch {
        Write-Status "Python installation failed: $_" "Error"
        return $false
    }
}

function Get-UserConfirmation {
    param(
        [string]$Question,
        [bool]$DefaultYes = $true
    )

    $prompt = if ($DefaultYes) { "[Y/n]" } else { "[y/N]" }
    Write-Host ""
    Write-Host "$Question $prompt " -ForegroundColor Yellow -NoNewline
    $response = Read-Host

    if ([string]::IsNullOrWhiteSpace($response)) {
        return $DefaultYes
    }

    return $response -match "^[Yy]"
}

function Show-Help {
    Write-Host ""
    Write-Host "IBS Compilers Bootstrap Script" -ForegroundColor Cyan
    Write-Host "===============================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "This script verifies Python is installed, then runs installer_windows.py" -ForegroundColor White
    Write-Host "to complete the Windows installation." -ForegroundColor White
    Write-Host ""
    Write-Host "Usage: .\bootstrap.ps1 [options]" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Options:" -ForegroundColor Yellow
    Write-Host "  -SkipPythonInstall  Don't attempt to install Python if missing"
    Write-Host "  -Help               Show this help message"
    Write-Host ""
    Write-Host "The installer_windows.py script supports additional options:" -ForegroundColor Yellow
    Write-Host "  --skip-freetds      Skip FreeTDS installation"
    Write-Host "  --skip-packages     Skip Python package installation"
    Write-Host "  --force             Force reinstallation of components"
    Write-Host ""
}

# =============================================================================
# MAIN
# =============================================================================

function Main {
    if ($Help) {
        Show-Help
        return
    }

    # Initialize log file (overwrites existing log)
    Initialize-LogFile
    Write-Status "Bootstrap script started" "Step"

    # Header
    Clear-Host
    Write-Host ""
    Write-Host "  =============================================" -ForegroundColor Cyan
    Write-Host "    IBS Compilers - Windows Bootstrap" -ForegroundColor Cyan
    Write-Host "  =============================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Project: $Script:ProjectRoot" -ForegroundColor Gray
    Write-Host "  Log file: $Script:LogFile" -ForegroundColor Gray
    Write-Host ""

    # Check for installer_windows.py
    if (-not (Test-Path $Script:InstallerScript)) {
        Write-Status "installer_windows.py not found at: $Script:InstallerScript" "Error"
        Write-Host ""
        Write-Host "  Please ensure installer_windows.py exists in the install directory." -ForegroundColor Yellow
        Write-Host ""
        return
    }

    # Step 1: Check Python
    Write-Host ""
    Write-Host "  --- Checking Python Installation ---" -ForegroundColor White
    Write-Host ""
    Write-Status "Checking for Python installation" "Step"

    $pythonInfo = Get-PythonInfo

    if ($null -eq $pythonInfo) {
        Write-Status "Python not found" "Warn"

        if ($SkipPythonInstall) {
            Write-Status "Skipping Python installation (user flag)" "Warn"
            Write-Status "Cannot proceed without Python" "Error"
            Write-Host ""
            Write-Host "  Cannot proceed without Python." -ForegroundColor Red
            Write-Host "  Please install Python $Script:RequiredPythonVersion or later." -ForegroundColor Yellow
            Write-Host ""
            return
        }

        if (Get-UserConfirmation "Python is required. Install Python 3.11 via winget?") {
            Write-Status "User approved Python installation" "Info"
            if (-not (Install-PythonViaWinget)) {
                Write-Status "Python installation failed" "Error"
                Write-Host ""
                Write-Host "  Please install Python manually and re-run this script." -ForegroundColor Yellow
                Write-Host ""
                return
            }

            # Refresh PATH and re-check
            Write-Status "Refreshing PATH and re-checking for Python" "Info"
            $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
            $pythonInfo = Get-PythonInfo

            if ($null -eq $pythonInfo) {
                Write-Status "Python still not found after installation" "Error"
                Write-Host ""
                Write-Host "  You may need to restart your terminal or computer." -ForegroundColor Yellow
                Write-Host "  Then re-run this bootstrap script." -ForegroundColor Yellow
                Write-Host ""
                return
            }
        } else {
            Write-Status "User declined Python installation" "Warn"
            Write-Host ""
            Write-Host "  Cannot proceed without Python." -ForegroundColor Red
            Write-Host ""
            return
        }
    }

    # Python found - check version
    Write-Status "Found: $($pythonInfo.VersionString)" "Success"
    Write-Status "Path: $($pythonInfo.Path)" "Info"
    Write-Status "Command: $($pythonInfo.Command)" "Info"

    if (-not (Test-PythonVersion $pythonInfo.Version)) {
        Write-Status "Python version $($pythonInfo.Version) is below minimum requirement ($Script:RequiredPythonVersion)" "Warn"

        if ($SkipPythonInstall) {
            Write-Host ""
            Write-Host "  Cannot proceed with outdated Python." -ForegroundColor Red
            Write-Host "  Please upgrade to Python $Script:RequiredPythonVersion or later." -ForegroundColor Yellow
            Write-Host ""
            return
        }

        if (Get-UserConfirmation "Install Python 3.11 via winget? (Your current version will remain installed)") {
            if (-not (Install-PythonViaWinget)) {
                Write-Host ""
                Write-Host "  Please upgrade Python manually and re-run this script." -ForegroundColor Yellow
                Write-Host ""
                return
            }

            # Refresh PATH and re-check
            $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
            $pythonInfo = Get-PythonInfo
        } else {
            Write-Host ""
            Write-Host "  Proceeding with Python $($pythonInfo.Version) (may have compatibility issues)" -ForegroundColor Yellow
            Write-Host ""
        }
    }

    Write-Status "Python $($pythonInfo.Version) meets requirements" "Success"

    # Step 2: Run installer_windows.py
    Write-Host ""
    Write-Host "  --- Launching Windows Installer ---" -ForegroundColor White
    Write-Host ""

    Write-Status "Launching installer_windows.py with $($pythonInfo.Command)" "Step"
    Write-Status "installer_windows.py will append to the same log file" "Info"
    Write-Host ""

    # Build the command
    $pythonCmd = $pythonInfo.Command
    $exitCode = 0

    try {
        if ($pythonCmd -eq "py -3") {
            Write-Status "Executing: py -3 $Script:InstallerScript" "Info"
            & py -3 $Script:InstallerScript
            $exitCode = $LASTEXITCODE
        } else {
            Write-Status "Executing: $pythonCmd $Script:InstallerScript" "Info"
            & $pythonCmd $Script:InstallerScript
            $exitCode = $LASTEXITCODE
        }

        if ($exitCode -ne 0) {
            Write-Status "installer_windows.py exited with code $exitCode" "Error"
            Write-Host ""
            Write-Host "  Check the log file for details: $Script:LogFile" -ForegroundColor Yellow
            Write-Host ""
            exit $exitCode
        } else {
            Write-Status "Bootstrap completed - installer_windows.py succeeded" "Success"
            Write-Status "See $Script:LogFile for full installation log" "Info"

            # Refresh PATH in current session
            Write-Host ""
            Write-Host "  --- Refreshing PATH ---" -ForegroundColor White
            Write-Host ""
            Write-Status "Refreshing PATH environment variable..." "Step"
            $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
            Write-Status "PATH refreshed in current session" "Success"

            # Verify commands are available
            $vimPath = Get-Command vim -ErrorAction SilentlyContinue
            $runsqlPath = Get-Command runsql -ErrorAction SilentlyContinue
            $tailPath = Get-Command tail -ErrorAction SilentlyContinue

            if ($vimPath) {
                Write-Status "vim is available: $($vimPath.Source)" "Success"
            } else {
                Write-Status "vim not found in PATH - you may need to restart your terminal" "Warn"
            }

            if ($tailPath) {
                Write-Status "tail is available: $($tailPath.Source)" "Success"
            } else {
                Write-Status "tail not found in PATH - you may need to restart your terminal" "Warn"
            }

            if ($runsqlPath) {
                # Get the Scripts directory (parent of runsql.exe)
                $scriptsDir = Split-Path $runsqlPath.Source -Parent
                Write-Status "IBS compilers are available: $scriptsDir" "Success"
            } else {
                Write-Status "IBS compilers not found in PATH - you may need to restart your terminal" "Warn"
            }

            # Final message
            Write-Host ""
            Write-Host ""
            Write-Host "  ============================================================" -ForegroundColor Cyan
            Write-Host "    Setup complete! Run ``set_profile`` to get started." -ForegroundColor Cyan
            Write-Host "  ============================================================" -ForegroundColor Cyan
            Write-Host ""
            Write-Host ""
        }
    } catch {
        Write-Status "installer_windows.py failed with exception: $_" "Error"
        Write-Status "See $Script:LogFile for details" "Error"
        exit 1
    }
}

# Run main function
Main
