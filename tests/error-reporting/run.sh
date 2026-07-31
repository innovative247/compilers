#!/usr/bin/env bash
# run.sh -- error-reporting corpus runner (Linux / macOS; also works in Git Bash).
#
# Every case in cases.tsv is run TWICE per platform: once as inline SQL through
# isqlline, once as a script file through runsql. Both must agree with cases.tsv.
#
# Usage:
#   ./run.sh --sybase GONZO:sbnpro
#   ./run.sh --sybase GONZO:sbnpro --mssql SRM_LOCAL:master --postgres PGTEST:pgtest
#   ./run.sh --sybase GONZO:sbnpro --bin ../../bin/linux-x64
#
# Omit a platform to skip it. Exit code is 0 only when every case passed.
# The PowerShell runner (run.ps1) reads the same cases.tsv and asserts the same things.

set -u

ROOT="$(cd "$(dirname "$0")" && pwd)"
SYBASE=""; MSSQL=""; POSTGRES=""; BIN=""

while [ $# -gt 0 ]; do
    case "$1" in
        --sybase)   SYBASE="$2";   shift 2 ;;
        --mssql)    MSSQL="$2";    shift 2 ;;
        --postgres) POSTGRES="$2"; shift 2 ;;
        --bin)      BIN="$2";      shift 2 ;;
        -h|--help)  sed -n '2,14p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [ -n "$BIN" ]; then
    ISQLLINE="$BIN/isqlline"; RUNSQL="$BIN/runsql"
else
    ISQLLINE="isqlline";      RUNSQL="runsql"
fi

PASS=0; FAIL=0; SKIP=0
FAILURES=""

# Inline form of a case file: drop the batch terminator and blank lines, collapse to one line.
inline_sql() {
    awk 'BEGIN{ORS=" "} {gsub(/\r$/,"")} tolower($0) ~ /^[ \t]*go[ \t]*$/ {next} /^[ \t]*$/ {next} {print}' "$1" \
        | sed 's/[ ]*$//'
}

# check_outcome <label> <expect> <must_contain> <exit-code> <output>; appends to FAILURES.
check_outcome() {
    local label="$1" expect="$2" must="$3" code="$4" out="$5" needle hits rest
    if [ "$expect" = "ok" ]; then
        if [ "$code" -ne 0 ]; then
            FAILURES="$FAILURES
  $label - expected success, got exit $code. output:
$out"
            return 1
        fi
        if printf '%s' "$out" | grep -qE '^[[:space:]]*Msg '; then
            FAILURES="$FAILURES
  $label - expected success but an error was reported. output:
$out"
            return 1
        fi
        if [ -n "$must" ] && [ "$must" != "-" ]; then
            rest="$must"
            while [ -n "$rest" ]; do
                needle="${rest%%|*}"
                if [ "$rest" = "$needle" ]; then rest=""; else rest="${rest#*|}"; fi
                if ! printf '%s' "$out" | grep -qF "$needle"; then
                    FAILURES="$FAILURES
  $label - expected '$needle' in the rendered output. output:
$out"
                    return 1
                fi
            done
        fi
        return 0
    fi

    needle="${expect#error:}"
    if [ "$code" -eq 0 ]; then
        FAILURES="$FAILURES
  $label - expected '$needle' and a non-zero exit, got exit 0. output:
$out"
        return 1
    fi
    hits=$(printf '%s' "$out" | grep -F -o "$needle" | wc -l | tr -d ' ')
    if [ "$hits" -eq 0 ]; then
        FAILURES="$FAILURES
  $label - expected '$needle' in the output. output:
$out"
        return 1
    fi
    if [ "$hits" -gt 1 ]; then
        FAILURES="$FAILURES
  $label - '$needle' reported $hits times; the error is being printed more than once. output:
$out"
        return 1
    fi
    return 0
}

run_platform() {
    local name="$1" target="$2" column="$3"
    [ -z "$target" ] && return 0

    local profile="${target%%:*}" database="${target#*:}"
    if [ -z "$profile" ] || [ -z "$database" ] || [ "$profile" = "$target" ]; then
        echo "--$name expects PROFILE:DATABASE (e.g. GONZO:sbnpro), got '$target'" >&2
        exit 2
    fi

    echo
    echo "--- $name ($profile.$database) ---"

    local id file expect must sqlfile inline out code problems
    while IFS=$'\t' read -r id file c_syb c_ms c_pg must; do
        case "$id" in '#'*|'id'|'') continue ;; esac
        case "$column" in
            2) expect="$c_syb" ;;
            3) expect="$c_ms"  ;;
            4) expect="$c_pg"  ;;
        esac

        if [ "$expect" = "n/a" ]; then
            echo "[SKIP]  $id"; SKIP=$((SKIP+1)); continue
        fi

        sqlfile="$ROOT/cases/$file"
        problems=0

        inline="$(inline_sql "$sqlfile")"
        out="$("$ISQLLINE" "$inline" "$database" "$profile" 2>&1)"; code=$?
        check_outcome "$id/isqlline" "$expect" "$must" "$code" "$out" || problems=1

        out="$("$RUNSQL" "$sqlfile" "$database" "$profile" --changelog:n 2>&1)"; code=$?
        check_outcome "$id/runsql" "$expect" "$must" "$code" "$out" || problems=1

        if [ "$problems" -eq 0 ]; then
            echo "[PASS]  $id"; PASS=$((PASS+1))
        else
            echo "[FAIL]  $id"; FAIL=$((FAIL+1))
        fi
    done < "$ROOT/cases.tsv"
}

CASE_COUNT=$(grep -vc -e '^[[:space:]]*#' -e '^id	' -e '^[[:space:]]*$' "$ROOT/cases.tsv" || true)
echo "=== Compilers error-reporting corpus ==="
echo "Cases    : $CASE_COUNT"
echo "Binaries : ${BIN:-PATH}"

if [ -z "$SYBASE" ] && [ -z "$MSSQL" ] && [ -z "$POSTGRES" ]; then
    echo
    echo "Nothing to do: pass at least one of --sybase / --mssql / --postgres as PROFILE:DATABASE."
    exit 2
fi

run_platform "Sybase"   "$SYBASE"   2
run_platform "Mssql"    "$MSSQL"    3
run_platform "Postgres" "$POSTGRES" 4

echo
echo "=== Summary ==="
[ "$FAIL" -gt 0 ] && echo "FAIL  $FAIL"
echo "PASS  $PASS"
[ "$SKIP" -gt 0 ] && echo "SKIP  $SKIP"

if [ "$FAIL" -gt 0 ]; then
    echo
    echo "Failures:$FAILURES"
    exit 1
fi
exit 0
