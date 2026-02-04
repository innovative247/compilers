# Windows Server 2022 Development Setup

## Prerequisites

- Windows Server 2022
- PowerShell running as Administrator

## Step 1: Open PowerShell as Administrator

Right-click the Start button → **Windows PowerShell (Admin)**

## Step 2: Install Dependencies

```powershell
# Download and install VCLibs dependency
Invoke-WebRequest -Uri "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx" -OutFile "$env:TEMP\VCLibs.appx"
Add-AppxPackage -Path "$env:TEMP\VCLibs.appx"

# Download and install Microsoft.UI.Xaml dependency
Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/Microsoft.UI.Xaml/2.8.6" -OutFile "$env:TEMP\microsoft.ui.xaml.zip"
Expand-Archive -Path "$env:TEMP\microsoft.ui.xaml.zip" -DestinationPath "$env:TEMP\microsoft.ui.xaml" -Force
Add-AppxPackage -Path "$env:TEMP\microsoft.ui.xaml\tools\AppX\x64\Release\Microsoft.UI.Xaml.2.8.appx"
```

## Step 3: Install WinGet (Windows Package Manager)

```powershell
# Download WinGet (App Installer)
Invoke-WebRequest -Uri "https://github.com/microsoft/winget-cli/releases/download/v1.7.10861/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle" -OutFile "$env:TEMP\winget.msixbundle"

# Install it
Add-AppxPackage -Path "$env:TEMP\winget.msixbundle"

# Verify installation
winget --version
```

## Step 4: Install Windows Terminal

```powershell
# Download Windows Terminal
Invoke-WebRequest -Uri "https://github.com/microsoft/terminal/releases/download/v1.19.10821.0/Microsoft.WindowsTerminal_1.19.10821.0_8wekyb3d8bbwe.msixbundle" -OutFile "$env:TEMP\WindowsTerminal.msixbundle"

# Install it
Add-AppxPackage -Path "$env:TEMP\WindowsTerminal.msixbundle"
```

## Step 5: Launch Terminal

Search for **Terminal** in the Start menu, or run:

```powershell
wt
```

## Step 6: Install Node.js

```powershell
# Download Node.js LTS installer
Invoke-WebRequest -Uri "https://nodejs.org/dist/v20.11.1/node-v20.11.1-x64.msi" -OutFile "$env:TEMP\nodejs.msi"

# Install silently
Start-Process msiexec.exe -ArgumentList "/i", "$env:TEMP\nodejs.msi", "/qn" -Wait

# Refresh PATH (or just restart Terminal)
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
```

## Step 7: Install Claude Code

```powershell
npm install -g @anthropic-ai/claude-code
```

Then launch and authenticate:

```powershell
claude
```

## Step 8: Install Git

```powershell
# Download Git for Windows
Invoke-WebRequest -Uri "https://github.com/git-for-windows/git/releases/download/v2.43.0.windows.1/Git-2.43.0-64-bit.exe" -OutFile "$env:TEMP\git-installer.exe"

# Install silently with default options
Start-Process -FilePath "$env:TEMP\git-installer.exe" -ArgumentList "/VERYSILENT", "/NORESTART" -Wait

# Refresh PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
```

## Step 9: Clone Compiler Repo

```powershell
cd $HOME
git clone https://github.com/innovative247/compilers.git
```

## Step 10: Install SVN (Subversion)

```powershell
# Download Apache Subversion (SlikSVN - command-line client)
Invoke-WebRequest -Uri "https://sliksvn.com/pub/Slik-Subversion-1.14.3-x64.msi" -OutFile "$env:TEMP\svn-installer.msi"

# Install silently
Start-Process msiexec.exe -ArgumentList "/i", "$env:TEMP\svn-installer.msi", "/qn" -Wait

# Refresh PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
```

## Step 11: Checkout SVN Repo

```powershell
mkdir -Force "$HOME\ir_local"
cd "$HOME\ir_local"
svn checkout http://10.130.61.10/work2/svn/repos/SBN_IR/trunk .
```

## Final: Install Compiler

See readme.md in the compilers repo for compiler installation instructions.

---

## Notes

- For the latest version of Windows Terminal, check https://github.com/microsoft/terminal/releases and update the download URL in Step 4.
- For the latest WinGet version, check https://github.com/microsoft/winget-cli/releases
- For the latest Git version, check https://github.com/git-for-windows/git/releases
- If clipboard/copy-paste stops working (common with RDP), restart the clipboard service:

```cmd
taskkill /f /im rdpclip.exe && rdpclip.exe
```
