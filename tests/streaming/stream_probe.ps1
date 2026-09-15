# Spawns a compiler tool with -O <tmpfile>, then tails the file and stamps
# every new line with elapsed seconds since the spawn. Verdict at the end:
#   STREAMING-PASS — tick lines arrived spread across >= 3s wallclock
#   STREAMING-FAIL — every line arrived in the final < 1s window (buffered)
#
# Usage:
#   stream_probe.ps1 -Cmd "isqlline" -CmdArgs @('exec sbnmaster..pro_streaming_test','sbnmaster','GONZO')
#   stream_probe.ps1 -Cmd "runsql"   -CmdArgs @('script.sql','sbnmaster','GONZO')

param(
  [Parameter(Mandatory=$true)] [string]   $Cmd,
  [Parameter(Mandatory=$true)] [string[]] $CmdArgs,
  [string] $Marker = 'streaming tick',
  [int]    $TimeoutSec = 30
)

$ErrorActionPreference = 'Stop'
$out = Join-Path $env:TEMP ("stream_probe_{0}.out" -f ([guid]::NewGuid().ToString('N')))
if (Test-Path $out) { Remove-Item $out -Force }
New-Item -ItemType File -Path $out | Out-Null

$allArgs = @($CmdArgs) + @('-O', $out)
$start = Get-Date
Write-Host "[probe] spawning: $Cmd $($allArgs -join ' ')"
Write-Host "[probe] outfile : $out"

$proc = Start-Process -FilePath $Cmd -ArgumentList $allArgs -PassThru -NoNewWindow `
  -RedirectStandardOutput "$out.stdout" -RedirectStandardError "$out.stderr"

$timestamps = New-Object System.Collections.Generic.List[double]
$lastSize = 0
$deadline = $start.AddSeconds($TimeoutSec)

while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
  Start-Sleep -Milliseconds 100
  $size = (Get-Item $out -ErrorAction SilentlyContinue).Length
  if ($size -gt $lastSize) {
    $now = (Get-Date) - $start
    $newLines = (Get-Content $out -ReadCount 0 -Encoding UTF8) | Select-Object -Skip ([math]::Max(0, ($timestamps.Count)))
    foreach ($line in $newLines) {
      "[{0,7:N3}s] {1}" -f $now.TotalSeconds, $line | Write-Host
      if ($line -match $Marker) { $timestamps.Add($now.TotalSeconds) }
    }
    $lastSize = $size
  }
}

if (-not $proc.HasExited) { $proc.Kill() ; Write-Host "[probe] TIMEOUT after ${TimeoutSec}s — killed process" }
else                       { $proc.WaitForExit() }

# Drain any final lines
$now = (Get-Date) - $start
$allLines = Get-Content $out -ReadCount 0 -Encoding UTF8
if ($allLines.Count -gt $timestamps.Count) {
  $tail = $allLines | Select-Object -Skip $timestamps.Count
  foreach ($line in $tail) {
    "[{0,7:N3}s] {1} (post-exit drain)" -f $now.TotalSeconds, $line | Write-Host
    if ($line -match $Marker) { $timestamps.Add($now.TotalSeconds) }
  }
}

Write-Host ""
Write-Host "[probe] exit code  : $($proc.ExitCode)"
Write-Host "[probe] marker hits: $($timestamps.Count)"
if ($timestamps.Count -ge 2) {
  $span = ($timestamps[-1] - $timestamps[0])
  Write-Host ("[probe] first→last : {0:N3}s" -f $span)
  if ($span -ge 3.0) {
    Write-Host "[probe] VERDICT    : STREAMING-PASS" -ForegroundColor Green
  } else {
    Write-Host "[probe] VERDICT    : STREAMING-FAIL (all marker lines clumped within ${span}s)" -ForegroundColor Red
  }
} else {
  Write-Host "[probe] VERDICT    : INCONCLUSIVE — fewer than 2 marker hits" -ForegroundColor Yellow
}

Remove-Item "$out.stdout","$out.stderr" -ErrorAction SilentlyContinue
Write-Host "[probe] outfile retained: $out"
