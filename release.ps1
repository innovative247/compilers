# release.ps1 - Bump version, publish, commit, push, and create GitHub release
# Usage: .\release.ps1 -Version X.Y.Z -Notes "Release notes"

param(
    [Parameter(Mandatory=$true)]
    [string]$Version,

    [Parameter(Mandatory=$true)]
    [string]$Notes
)

Set-Location $PSScriptRoot

# 1. Publish (also bumps version in Directory.Build.props)
# publish.ps1 exits non-zero on Windows when the tar step fails; expected,
# since we create the linux/osx archives manually in step 2.
Write-Host "=== Publishing v$Version ===" -ForegroundColor Cyan
.\publish.ps1 -Version $Version
if ($LASTEXITCODE -ne 0) {
    if (-not (Test-Path (Join-Path $PSScriptRoot "bin\win-x64"))) {
        Write-Host "publish.ps1 failed - bin\win-x64 not found" -ForegroundColor Red
        exit 1
    }
    Write-Host "  (publish.ps1 tar step failed as expected on Windows; continuing)" -ForegroundColor Yellow
}

# 2. Windows tar workaround - create linux/osx archives manually
$binDir = Join-Path $PSScriptRoot "bin"
Write-Host ""
Write-Host "=== Creating Linux/macOS archives ===" -ForegroundColor Cyan

Push-Location $binDir
tar -czf compilers-net8-linux-x64.tar.gz -C linux-x64 .
if ($LASTEXITCODE -ne 0) { Write-Host "Failed to create linux tar.gz" -ForegroundColor Red; Pop-Location; exit 1 }
Write-Host "  Created: compilers-net8-linux-x64.tar.gz" -ForegroundColor Green

tar -czf compilers-net8-osx-x64.tar.gz -C osx-x64 .
if ($LASTEXITCODE -ne 0) { Write-Host "Failed to create osx tar.gz" -ForegroundColor Red; Pop-Location; exit 1 }
Write-Host "  Created: compilers-net8-osx-x64.tar.gz" -ForegroundColor Green
Pop-Location

# 3. Commit all tracked changes
Write-Host ""
Write-Host "=== Committing ===" -ForegroundColor Cyan
git add -u
git commit -m "$Notes (v$Version)" -m "Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
if ($LASTEXITCODE -ne 0) { Write-Host "git commit failed" -ForegroundColor Red; exit 1 }

# 4. Push
Write-Host ""
Write-Host "=== Pushing ===" -ForegroundColor Cyan
git push origin main
if ($LASTEXITCODE -ne 0) { Write-Host "git push failed" -ForegroundColor Red; exit 1 }

# 5. GitHub release
Write-Host ""
Write-Host "=== Creating GitHub release ===" -ForegroundColor Cyan
gh release create "v$Version" `
    bin/compilers-net8-win-x64.zip `
    bin/compilers-net8-linux-x64.tar.gz `
    bin/compilers-net8-osx-x64.tar.gz `
    --title "v$Version" `
    --notes $Notes

Write-Host ""
Write-Host "Released v$Version" -ForegroundColor Green
