# Builds the Compilers VS Code extension (.vsix). The version always follows src/Directory.Build.props.
# Usage: ./package.ps1 [-OutDir <folder>]   (default: this folder)
param([string]$OutDir = $PSScriptRoot)
$ErrorActionPreference = 'Stop'

$props = Join-Path $PSScriptRoot '..\src\Directory.Build.props'
$version = ([xml](Get-Content -Raw $props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $props" }

# Stamp the version into package.json for the build only (text replace keeps the formatting); restored in finally.
$pkgPath = Join-Path $PSScriptRoot 'package.json'
$pkg = Get-Content -Raw $pkgPath
$stamped = [regex]::Replace($pkg, '("version"\s*:\s*")[^"]*(")', "`${1}$version`${2}", 1)
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($pkgPath, $stamped, $utf8)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$out = Join-Path (Resolve-Path $OutDir) "compilers-$version.vsix"

Push-Location $PSScriptRoot
try {
    npx --yes '@vscode/vsce' package --no-dependencies --skip-license -o $out
    if ($LASTEXITCODE -ne 0) { throw "vsce failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
    # The committed package.json keeps its placeholder version; only the packaged copy is stamped.
    [System.IO.File]::WriteAllText($pkgPath, $pkg, $utf8)
}

Get-Item $out | Select-Object Name, Length
