# Win-x64 full sweep: same scope as wsl_full_sweep.sh but invoked from PowerShell.
$ErrorActionPreference = 'Continue'
$pass = 0; $fail = 0; $failList = @()

function Run([string]$name, [scriptblock]$cmd) {
  $out = & $cmd 2>&1
  $rc = $LASTEXITCODE
  if ($rc -eq 0) {
    $script:pass++
    $first = ($out | Select-Object -First 1) -as [string]
    "  {0,-32} PASS  {1}" -f $name, $first
  } else {
    $script:fail++
    $script:failList += "$name (rc=$rc)"
    "  {0,-32} FAIL  rc={1}" -f $name, $rc
    $out | Select-Object -First 5 | ForEach-Object { "      $_" }
  }
}

$ALL = @('runsql','isqlline','set_profile','iwho','iwatch','iplan','iplanext','runcreate',
         'eact','set_actions','eloc','set_table_locations','eopt','set_options',
         'compile_msg','set_messages','compile_required_fields','set_required_fields',
         'i_run_upgrade','transfer_data','bcp_data','extract_msg')

"=== Phase 1: 'version' on all 22 compilers ==="
foreach ($c in $ALL) { Run "$c version" { & $c version } }

"`n=== Phase 2: 'configure' on all 22 compilers ==="
foreach ($c in $ALL) { Run "$c configure" { & $c configure } }

"`n=== Phase 3: read-only DB tests against GONZO (full options — exercises CSS/Setup) ==="
Run "isqlline @@version (GONZO)"  { isqlline 'select @@version' master GONZO }
Run "iwho GONZO"                   { iwho GONZO }
Run "set_profile --view GONZO"     { set_profile --view GONZO }
Run "set_profile --test GONZO sql-source" { set_profile --test GONZO --what sql-source }

"`n=== Phase 4: streaming + runcreate ==="
Run "isqlline streaming proc"     { isqlline 'exec sbnmaster..pro_streaming_test' sbnmaster GONZO }
Run "runsql exec_streaming"        { runsql "$PSScriptRoot\exec_streaming_test.sql" sbnmaster GONZO }
Run "runcreate create_test"        { runcreate "$PSScriptRoot\create_test" GONZO }

"`n================================"
"TOTAL  PASS=$pass  FAIL=$fail"
if ($failList.Count -gt 0) { "FAILED:"; $failList | ForEach-Object { "  - $_" } }
