#!/bin/bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Sweep against the ISOLATED WSL install at /home/jwilliams/ibs-compilers
# (NOT the dev tree at /mnt/c/_innovative/_source/compilers/bin/linux-x64).
# Proves the published v2.0.70 artifact actually works on a real Linux box.
set -u
BIN=/home/jwilliams/ibs-compilers
cd "$BIN"

PASS=0; FAIL=0; FAIL_LIST=()

run() {
  local name="$1"; shift
  local out
  out=$("$@" 2>&1)
  local rc=$?
  if [ $rc -eq 0 ]; then
    PASS=$((PASS+1))
    printf "  %-32s PASS  %s\n" "$name" "$(echo "$out" | head -1)"
  else
    FAIL=$((FAIL+1))
    FAIL_LIST+=("$name (rc=$rc)")
    printf "  %-32s FAIL  rc=%d\n" "$name" "$rc"
    echo "$out" | head -3 | sed 's/^/      /'
  fi
}

echo "=== install location: $BIN ==="
echo "=== version: $($BIN/runsql version) ==="
echo ""

ALL=(runsql isqlline set_profile iwho iwatch iplan iplanext runcreate
     eact set_actions eloc set_table_locations eopt set_options
     compile_msg set_messages compile_required_fields set_required_fields
     i_run_upgrade transfer_data bcp_data extract_msg)

echo "=== Phase 1: version on all 22 ==="
for c in "${ALL[@]}"; do run "${c} version" "$BIN/${c}" version; done

echo
echo "=== Phase 2: read-only DB tests ==="
run "isqlline @@version GONZO_WSL"  "$BIN/isqlline" 'select @@version' master GONZO_WSL
run "iwho GONZO_WSL"                 "$BIN/iwho" GONZO_WSL
run "set_profile --view GONZO_WSL"   "$BIN/set_profile" --view GONZO_WSL

echo
echo "=== Phase 3: streaming + finite (the v2.0.70 changes) ==="
run "isqlline streaming proc"  "$BIN/isqlline" 'exec sbnmaster..pro_streaming_test' sbnmaster GONZO_WSL
run "runsql exec_streaming"    "$BIN/runsql"   "$SCRIPT_DIR/exec_streaming_test.sql" sbnmaster GONZO_WSL
run "runcreate create_test_wsl" "$BIN/runcreate" "$SCRIPT_DIR/create_test_wsl" GONZO_WSL

echo
echo "=== Phase 4: case-insensitive walk against /home/jwilliams/ir_local ==="
run "runcreate ir_local lowercase" "$BIN/runcreate" "$SCRIPT_DIR/create_test_ir_local" GONZO_HOME

echo
echo "================================"
echo "TOTAL  PASS=$PASS  FAIL=$FAIL"
if [ ${#FAIL_LIST[@]} -gt 0 ]; then
  echo "FAILED:"
  for f in "${FAIL_LIST[@]}"; do echo "  - $f"; done
fi
