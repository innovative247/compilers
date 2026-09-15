#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Full-22 compiler sweep on WSL (Ubuntu, native ext4-backed binaries on /mnt/c).
# 1) `version` for every binary — must exit 0
# 2) configure for every binary — must exit 0 (prints env diagnostics)
# 3) Read-only DB tests against GONZO_WSL (RAW_MODE) using JAKE/ibsibs2
# 4) Read-only DB tests against GONZO (full options) — proves Linux-side options-file load
# Skips: set_*, compile_* (interactive write paths), transfer_data (destructive),
#        bcp_data (data extract), iwatch (background tail).
set -u
cd /mnt/c/_innovative/_source/compilers/bin/linux-x64
chmod +x ./* 2>/dev/null

PASS=0
FAIL=0
FAIL_LIST=()

run() {
  local name="$1"; shift
  local out
  out=$("$@" 2>&1)
  local rc=$?
  if [ $rc -eq 0 ]; then
    PASS=$((PASS+1))
    printf "  %-30s PASS  %s\n" "$name" "$(echo "$out" | head -1)"
  else
    FAIL=$((FAIL+1))
    FAIL_LIST+=("$name (rc=$rc)")
    printf "  %-30s FAIL  rc=%d\n%s\n" "$name" "$rc" "$out" | sed 's/^/      /'
  fi
}

ALL=(runsql isqlline set_profile iwho iwatch iplan iplanext runcreate
     eact set_actions eloc set_table_locations eopt set_options
     compile_msg set_messages compile_required_fields set_required_fields
     i_run_upgrade transfer_data bcp_data extract_msg)

echo "=== Phase 1: 'version' on all 22 compilers ==="
for c in "${ALL[@]}"; do
  run "${c} version" "./${c}" version
done

echo
echo "=== Phase 2: 'configure' on all 22 compilers ==="
for c in "${ALL[@]}"; do
  run "${c} configure" "./${c}" configure
done

echo
echo "=== Phase 3: read-only DB tests (GONZO_WSL = JAKE/ibsibs2 + RAW_MODE) ==="
run "isqlline @@version" ./isqlline 'select @@version' master GONZO_WSL
run "isqlline sysobjects" ./isqlline 'select top 3 name from sysobjects where type='U'' master GONZO_WSL
run "iwho"                ./iwho GONZO_WSL
run "iplan @@version"     ./iplan 'select @@version' master GONZO_WSL
run "set_profile --view"  ./set_profile --view GONZO_WSL

echo
echo "=== Phase 4: GONZO with full options (Linux SQL_SOURCE: /mnt/c/.../current.sql) ==="
run "isqlline @@version (GONZO)" ./isqlline 'select @@version' master GONZO

echo
echo "=== Phase 5: streaming + runcreate (already proven, repeat for the report) ==="
run "isqlline streaming proc" ./isqlline 'exec sbnmaster..pro_streaming_test' sbnmaster GONZO_WSL
run "runsql exec_streaming"   ./runsql "$SCRIPT_DIR/exec_streaming_test.sql" sbnmaster GONZO_WSL
run "runcreate create_test_wsl" ./runcreate "$SCRIPT_DIR/create_test_wsl" GONZO_WSL

echo
echo "================================"
echo "TOTAL  PASS=$PASS  FAIL=$FAIL"
if [ ${#FAIL_LIST[@]} -gt 0 ]; then
  echo "FAILED:"
  for f in "${FAIL_LIST[@]}"; do echo "  - $f"; done
fi
