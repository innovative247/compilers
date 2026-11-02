# Builds the extension once and copies it as compilers.vsix (fixed name, so updates replace it) into each bin/<rid>; fails if node/npx is missing.
# Usage: ./bundle.ps1 -Dest bin\win-x64[,bin\linux-x64,...]
param([Parameter(Mandatory)][string[]]$Dest)
$ErrorActionPreference = 'Stop'

foreach ($tool in 'node', 'npx') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool not found on PATH. Node.js is required to build the VS Code extension (vscode/compilers.vsix is part of every build)."
    }
}

$build = Join-Path $PSScriptRoot 'dist'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
try {
    & (Join-Path $PSScriptRoot 'package.ps1') -OutDir $build | Out-Null
    $vsix = Get-ChildItem $build -Filter 'compilers-*.vsix' | Select-Object -First 1
    if (-not $vsix) { throw "package.ps1 produced no .vsix in $build" }
    foreach ($d in $Dest) {
        New-Item -ItemType Directory -Force -Path $d | Out-Null
        Copy-Item $vsix.FullName (Join-Path $d 'compilers.vsix') -Force
    }
    Write-Host "  VS Code extension $($vsix.Name) -> compilers.vsix in $($Dest.Count) folder(s)" -ForegroundColor Green
} finally {
    if (Test-Path $build) { Remove-Item $build -Recurse -Force }
}
