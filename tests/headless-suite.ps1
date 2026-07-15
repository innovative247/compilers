<#
.SYNOPSIS
    Headless-mode regression suite for the IBS compilers.

.DESCRIPTION
    One test per outcome row in compilers/docs/feature-map.md. The suite
    verifies that an AI agent can drive every command without a TTY.

    Mutating tests run against a throwaway TEST_LOCAL profile (cloned from
    SRM_LOCAL) and a scratch SQL-source directory under $env:TEMP. Read-only
    tests run against SRM_LOCAL directly.

    transfer_data is intentionally NEVER invoked — team policy.

.PARAMETER OnlyId
    Run only tests whose ID matches this pattern (e.g. 'set_options.*').

.PARAMETER NoCleanup
    Keep TEST_LOCAL profile and scratch dir for post-mortem inspection.

.PARAMETER ListOnly
    Print every test ID + status and exit.

.NOTES
    Pair with feature-map.md — the test IDs there match the ones below.
#>
[CmdletBinding()]
param(
    [string]$OnlyId,
    [switch]$NoCleanup,
    [switch]$ListOnly
)

$ErrorActionPreference = 'Stop'
$script:Results = [System.Collections.ArrayList]::new()
$script:Bin     = (Resolve-Path "$PSScriptRoot\..\bin\win-x64").Path
$script:Scratch = Join-Path $env:TEMP "compilers-tests-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
$script:TestProfile  = 'TEST_LOCAL'
$script:SourceProfile = 'SRM_LOCAL'

# ---------- test harness ----------

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][ValidateSet('PASS','FAIL','SKIP','GAP')][string]$Status,
        [string]$Reason = ''
    )
    [void]$script:Results.Add([pscustomobject]@{ Id = $Id; Status = $Status; Reason = $Reason })
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'SKIP' { 'DarkGray' }
        'GAP'  { 'Yellow' }
    }
    $tag = "[$Status]".PadRight(7)
    if ($Reason) { Write-Host "$tag $Id - $Reason" -ForegroundColor $color }
    else         { Write-Host "$tag $Id"          -ForegroundColor $color }
}

function Test-Case {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][scriptblock]$Body
    )
    if ($OnlyId -and $Id -notlike $OnlyId) { return }
    if ($ListOnly)                          { Add-Result -Id $Id -Status 'SKIP' -Reason 'listed'; return }

    try {
        & $Body
        # If body didn't throw and didn't add a result, mark PASS
        if (-not ($script:Results | Where-Object Id -eq $Id)) {
            Add-Result -Id $Id -Status 'PASS'
        }
    } catch {
        Add-Result -Id $Id -Status 'FAIL' -Reason $_.Exception.Message
    }
}

function Skip-Case {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Reason,
        [ValidateSet('SKIP','GAP')][string]$As = 'SKIP'
    )
    if ($OnlyId -and $Id -notlike $OnlyId) { return }
    Add-Result -Id $Id -Status $As -Reason $Reason
}

function Invoke-Cli {
    <# Runs a compiler exe; returns [pscustomobject]@{ ExitCode; StdOut; StdErr }. #>
    param(
        [Parameter(Mandatory)][string]$Exe,
        [Parameter(ValueFromRemainingArguments)][string[]]$Args
    )
    $exePath = Join-Path $script:Bin "$Exe.exe"
    if (-not (Test-Path $exePath)) { throw "binary not found: $exePath" }

    $stdoutFile = [IO.Path]::GetTempFileName()
    $stderrFile = [IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath $exePath -ArgumentList $Args `
                -NoNewWindow -Wait -PassThru `
                -RedirectStandardOutput $stdoutFile `
                -RedirectStandardError  $stderrFile
        [pscustomobject]@{
            ExitCode = $proc.ExitCode
            StdOut   = (Get-Content $stdoutFile -Raw -ErrorAction SilentlyContinue) -as [string]
            StdErr   = (Get-Content $stderrFile -Raw -ErrorAction SilentlyContinue) -as [string]
        }
    } finally {
        Remove-Item $stdoutFile, $stderrFile -ErrorAction SilentlyContinue
    }
}

function Assert-ExitCode {
    param([Parameter(Mandatory)]$Result, [int]$Expected = 0)
    if ($Result.ExitCode -ne $Expected) {
        throw "exit code = $($Result.ExitCode), expected $Expected. stderr: $($Result.StdErr)"
    }
}

function Assert-FileContains {
    param([string]$Path, [string]$Pattern)
    if (-not (Test-Path $Path)) { throw "file missing: $Path" }
    $content = Get-Content $Path -Raw
    if ($content -notmatch [regex]::Escape($Pattern)) {
        throw "pattern not found in $Path : $Pattern"
    }
}

function Get-Settings {
    <# Reads settings.json next to the binaries. Returns a PSCustomObject. #>
    $path = Join-Path $script:Bin 'settings.json'
    if (-not (Test-Path $path)) { throw "settings.json not found at $path" }
    return Get-Content $path -Raw | ConvertFrom-Json
}

function Get-Profile {
    param([Parameter(Mandatory)][string]$Name)
    $settings = Get-Settings
    if (-not $settings.Profiles.$Name) { return $null }
    return $settings.Profiles.$Name
}

function Assert-ProfileExists {
    param([Parameter(Mandatory)][string]$Name)
    if (-not (Get-Profile $Name)) { throw "profile '$Name' not found in settings.json" }
}

function Assert-ProfileAbsent {
    param([Parameter(Mandatory)][string]$Name)
    if (Get-Profile $Name) { throw "profile '$Name' should have been removed from settings.json" }
}

function Assert-Field {
    param([Parameter(Mandatory)]$Profile, [Parameter(Mandatory)][string]$Field, $Expected)
    $actual = $Profile.$Field
    if ($actual -ne $Expected) {
        throw "profile.$Field = '$actual', expected '$Expected'"
    }
}

# ---------- setup ----------

function Initialize-Suite {
    Write-Host "=== Compilers Headless Suite ===" -ForegroundColor Cyan
    Write-Host "Binaries : $script:Bin"
    Write-Host "Scratch  : $script:Scratch"
    Write-Host ""

    # Build scratch SQL source with minimum files for compile commands to find paths.
    $setup = Join-Path $script:Scratch 'CSS\Setup'
    New-Item -ItemType Directory -Path $setup -Force | Out-Null

    # options.def — minimal valid file (value option + onoff option)
    @(
        '# Test options.def for headless suite'
        'v:testopt <<defaultvalue>> Test value option'
        'c:testflag - Test onoff option'
    ) | Set-Content -Path (Join-Path $setup 'options.def') -Encoding UTF8

    # options.101 — company file (must exist for Options.GenerateOptionFiles)
    @(
        '# Test options.101'
        'v:testopt <<companyvalue>> Test value option'
        'c:testflag + Test onoff option'
    ) | Set-Content -Path (Join-Path $setup 'options.101') -Encoding UTF8

    # table_locations — must exist
    @(
        '-> ba_basic_users     &users&   Users table'
        '-> ba_options         &options& Options table'
    ) | Set-Content -Path (Join-Path $setup 'table_locations') -Encoding UTF8

    # actions / actions_dtl
    'placeholder header'  | Set-Content -Path (Join-Path $setup 'actions')        -Encoding UTF8
    'placeholder detail'  | Set-Content -Path (Join-Path $setup 'actions_dtl')    -Encoding UTF8

    # required_fields / required_fields_dtl
    'placeholder header'  | Set-Content -Path (Join-Path $setup 'css.required_fields')     -Encoding UTF8
    'placeholder detail'  | Set-Content -Path (Join-Path $setup 'css.required_fields_dtl') -Encoding UTF8

    # Message files - compile_msg checks every extension exists before running.
    # Empty placeholders are sufficient for codepath-reach assertions.
    foreach ($ext in @('.ibs_msg','.ibs_msgrp','.jam_msg','.jam_msgrp',
                       '.sqr_msg','.sqr_msgrp','.sql_msg','.sql_msgrp',
                       '.gui_msg','.gui_msgrp')) {
        New-Item -ItemType File -Path (Join-Path $setup "css$ext") -Force | Out-Null
    }

    # Fake editor for SKIP-AGENT regression tests.
    #
    # AGENT CONTRACT: an agent NEVER passes --edit-header / --edit-detail.
    # The agent writes source files directly (options.def, actions, etc.)
    # then invokes the command with --skip-edit --compile. See feature-map.md
    # "Agent contract" section.
    #
    # The --edit-* flags still exist in the binary for HUMAN use (they launch
    # $EDITOR on the file and wait). We test them anyway as non-regression
    # guards: a .cmd that touches a marker file and exits, pointed at by
    # $env:EDITOR. If a future change breaks the editor-launch codepath,
    # human users would notice; these tests catch it.
    $script:FakeEditor = Join-Path $script:Scratch 'fake-editor.cmd'
    $script:EditorMarker = Join-Path $script:Scratch 'editor-marker.txt'
    @(
        '@echo off'
        "echo edited:%~1 >> ""$script:EditorMarker"""
    ) | Set-Content -Path $script:FakeEditor -Encoding ASCII
    $script:OriginalEditor = $env:EDITOR
    $env:EDITOR = $script:FakeEditor

    # Clone SRM_LOCAL -> TEST_LOCAL, then edit to point at scratch sql-source.
    Write-Host "Cloning $script:SourceProfile -> $script:TestProfile ..." -ForegroundColor DarkGray
    $copy = Invoke-Cli set_profile '--copy' $script:SourceProfile '--to' $script:TestProfile
    if ($copy.ExitCode -ne 0) {
        # Maybe it already exists from a previous run — try delete + retry.
        Invoke-Cli set_profile '--delete' $script:TestProfile '--yes' | Out-Null
        $copy = Invoke-Cli set_profile '--copy' $script:SourceProfile '--to' $script:TestProfile
        if ($copy.ExitCode -ne 0) { throw "could not clone $script:SourceProfile: $($copy.StdErr)" }
    }
    # SRM_LOCAL is RAW_MODE in real settings (Atlas/SRM has no CSS/Setup tree).
    # Flip TEST_LOCAL out of raw mode so the SBN-flavored test paths (options,
    # table_locations, changelog, symlinks) actually exercise their code.
    $edit = Invoke-Cli set_profile '--edit' $script:TestProfile `
        '--no-raw' '--company' '101' '--sql-source' $script:Scratch
    if ($edit.ExitCode -ne 0) { throw "could not edit TEST_LOCAL: $($edit.StdErr)" }
}

function Remove-Suite {
    $env:EDITOR = $script:OriginalEditor
    if ($NoCleanup) {
        Write-Host "Leaving TEST_LOCAL and $script:Scratch in place (--NoCleanup)." -ForegroundColor Yellow
        return
    }
    try { Invoke-Cli set_profile '--delete' $script:TestProfile '--yes' | Out-Null } catch {}
    if (Test-Path $script:Scratch) {
        Remove-Item $script:Scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------- tests ----------

function Test-AdHocQueries {
    Write-Host "`n--- 1. Ad-hoc query / process inspection ---" -ForegroundColor Cyan

    # ===== isqlline =====
    # NB: arg order per Program.cs Usage is <SQL> <database> <server/profile>.
    # PS5 Start-Process -ArgumentList splits args with spaces, so the SQL must
    # be space-free. SELECT(1+1) parses as a valid expression returning 2.
    Test-Case 'isqlline.basic' {
        $r = Invoke-Cli isqlline 'SELECT(1+1)' 'master' $script:SourceProfile
        Assert-ExitCode $r
        if ($r.StdOut -notmatch '\b2\b' -or $r.StdOut -notmatch '1 row affected') {
            throw "expected '2' + '1 row affected' in output. stdout: $($r.StdOut)"
        }
    }
    Test-Case 'isqlline.echo' {
        $r = Invoke-Cli isqlline 'SELECT(1+1)' 'master' $script:SourceProfile '-E'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch '1> SELECT\(1\+1\)') { throw "expected echo line numbers in output. stdout: $($r.StdOut)" }
    }
    Test-Case 'isqlline.outfile' {
        $out = Join-Path $script:Scratch 'isqlline.out'
        Remove-Item $out -Force -ErrorAction SilentlyContinue
        $r = Invoke-Cli isqlline 'SELECT(1+1)' 'master' $script:SourceProfile "-O$out"
        Assert-ExitCode $r
        if (-not (Test-Path $out)) { throw "outfile not created" }
        $content = Get-Content $out -Raw
        if ($content -notmatch '\b2\b' -or $content -notmatch '1 row affected') {
            throw "outfile lacks expected query result: $content"
        }
    }
    Test-Case 'isqlline.platform' {
        $r = Invoke-Cli isqlline 'SELECT(1+1)' 'master' $script:SourceProfile '-MSSQL'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch '\b2\b') { throw "expected '2' in output" }
    }
    Test-Case 'isqlline.platform_pg' {
        # -PG is parsed identically to -MSSQL/-SYBASE (Common.cs FindAndRemove_SQLServerType),
        # but unlike -MSSQL against SRM_LOCAL (already MSSQL, so the override is a no-op),
        # ProfileManager.Resolve honors a non-default cmdvars.ServerType unconditionally
        # (ProfileManager.cs:148) — so -PG genuinely forces a PostgresExecutor against
        # SRM_LOCAL's real MSSQL server. There's no live PG target in this suite, so the
        # only thing provable offline is that the flag is parsed/dispatched (a real connection
        # attempt that fails on protocol mismatch), not rejected as a usage/parse error.
        $r = Invoke-Cli isqlline 'SELECT(1+1)' 'master' $script:SourceProfile '-PG'
        if ($r.ExitCode -eq 0) { throw '-PG against a non-PG server should not succeed' }
        if ($r.StdOut -match 'Usage:') { throw "-PG should be consumed as a valid flag, not rejected as bad usage. stdout: $($r.StdOut)" }
        if ($r.StdOut -notmatch 'ERROR! Failed to Run\.') { throw "expected a dispatched-but-failed connection attempt, not a parse error. stdout: $($r.StdOut)" }
    }
    Test-Case 'isqlline.error_bad_profile' {
        $r = Invoke-Cli isqlline 'SELECT(1+1)' 'master' 'NO_SUCH_PROFILE_XYZ'
        if ($r.ExitCode -eq 0) { throw 'unknown profile should exit non-zero' }
    }

    # ===== iwho =====
    Test-Case 'iwho.all' {
        $r = Invoke-Cli iwho $script:SourceProfile
        Assert-ExitCode $r
        # iwho prints column headers: spid, status, login, ...
        if ($r.StdOut -notmatch 'spid' -and $r.StdOut -notmatch 'login') {
            throw "expected column headers in output. stdout: $($r.StdOut)"
        }
    }
    Test-Case 'iwho.spid' {
        # Filter by a SPID that probably exists; if not, at least it shouldn't crash
        $r = Invoke-Cli iwho $script:SourceProfile '1'
        Assert-ExitCode $r
    }
    Test-Case 'iwho.login' {
        $r = Invoke-Cli iwho $script:SourceProfile 'sa%'
        Assert-ExitCode $r
    }
    Test-Case 'iwho.error_bad_profile' {
        $r = Invoke-Cli iwho 'NO_SUCH_PROFILE_XYZ'
        if ($r.ExitCode -eq 0) { throw 'unknown profile should exit non-zero' }
    }
    Skip-Case 'iwho.timer' 'polling test would block the suite indefinitely; manual verification only'

    # ===== iplan / iplanext =====
    Test-Case 'iplan.basic' {
        # SPID 1 may or may not be active; iplan prints "no active server process" diagnostic
        # if not. Either way the agent gets a deterministic exit + recognizable output.
        $r = Invoke-Cli iplan $script:SourceProfile '1'
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'no active|spid|plan|query_plan|session_id|terminated') {
            throw "no recognized iplan diagnostic. stdout: $($r.StdOut) stderr: $($r.StdErr)"
        }
    }
    Test-Case 'iplan.database' {
        $r = Invoke-Cli iplan $script:SourceProfile '1' '-D' 'master'
        Assert-ExitCode $r
    }
    Test-Case 'iplan.error_missing_spid' {
        $r = Invoke-Cli iplan $script:SourceProfile
        if ($r.ExitCode -eq 0) { throw 'iplan without spid should fail' }
        if ($r.StdErr -notmatch 'Usage') { throw "stderr should print usage. stderr: $($r.StdErr)" }
    }
    Test-Case 'iplan.error_bad_profile' {
        $r = Invoke-Cli iplan 'NO_SUCH_PROFILE_XYZ' '1'
        if ($r.ExitCode -eq 0) { throw 'unknown profile should exit non-zero' }
    }
    Test-Case 'iplanext.basic' {
        # iplanext writes PLANTRACE.<profile>.<spid>.<pid> file with three sections
        $r = Invoke-Cli iplanext $script:SourceProfile '1'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch 'PLANTRACE') { throw "expected PLANTRACE marker in output. stdout: $($r.StdOut)" }
        if ($r.StdOut -notmatch 'Process Info') { throw "expected 'Process Info' section in output. stdout: $($r.StdOut)" }
    }
    Test-Case 'iplanext.error_missing_spid' {
        $r = Invoke-Cli iplanext $script:SourceProfile
        if ($r.ExitCode -eq 0) { throw 'iplanext without spid should fail' }
    }
}

function Test-ScriptExecution {
    Write-Host "`n--- 2. Script execution ---" -ForegroundColor Cyan

    # SRM_LOCAL is raw mode (no Options replacement); SELECT 1 works as-is.
    $sql = Join-Path $script:Scratch 'select1.sql'
    'SELECT 1' | Set-Content -Path $sql -Encoding UTF8

    # ===== runsql =====
    Test-Case 'runsql.basic' {
        $r = Invoke-Cli runsql $sql 'master' $script:SourceProfile '--changelog:n'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch 'return status = 0') { throw "expected 'return status = 0'. stdout: $($r.StdOut)" }
    }
    Test-Case 'runsql.echo' {
        $r = Invoke-Cli runsql $sql 'master' $script:SourceProfile '-E' '--changelog:n'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch '1> SELECT 1') { throw "expected echoed line. stdout: $($r.StdOut)" }
    }
    Test-Case 'runsql.preview' {
        # --preview prints compiled SQL to stdout but does NOT execute the batches.
        $r = Invoke-Cli runsql $sql 'master' $script:SourceProfile '--preview' '--changelog:n'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch 'SELECT 1') { throw "preview should include the SQL. stdout: $($r.StdOut)" }
        if ($r.StdOut -match 'return status = 0') { throw "preview should NOT execute (saw 'return status = 0')" }
    }
    Test-Case 'runsql.no_changelog' {
        # --changelog:n should suppress the ba_gen_chg_log_new exec
        $r = Invoke-Cli runsql $sql 'master' $script:SourceProfile '--changelog:n'
        Assert-ExitCode $r
        if ($r.StdOut -match 'ba_gen_chg_log_new') { throw "--changelog:n should suppress changelog SQL but it appeared" }
    }
    Test-Case 'runsql.outfile' {
        # runsql -O writes to the named file directly (no auto-suffix).
        # Only runcreate appends .out / .err. Don't conflate the two.
        $out = Join-Path $script:Scratch 'runsql.log'
        Remove-Item $out -Force -ErrorAction SilentlyContinue
        $r = Invoke-Cli runsql $sql 'master' $script:SourceProfile "-O$out" '--changelog:n'
        Assert-ExitCode $r
        if (-not (Test-Path $out)) { throw "outfile not created at $out" }
        $content = Get-Content $out -Raw
        if ($content -notmatch 'return status = 0') { throw "outfile lacks 'return status = 0'" }
    }
    Test-Case 'runsql.seq' {
        # Sequence loop: -F1 -L3 should run the script three times.
        $r = Invoke-Cli runsql $sql 'master' $script:SourceProfile '-F1' '-L3' '--changelog:n'
        Assert-ExitCode $r
        # Each iteration prints "Running N of 3:" — count occurrences
        $runs = ([regex]::Matches($r.StdOut, 'Running \d+ of 3')).Count
        if ($runs -ne 3) { throw "expected 3 sequenced runs, saw $runs. stdout: $($r.StdOut)" }
    }
    Test-Case 'runsql.error_bad_script' {
        $r = Invoke-Cli runsql 'no-such-file.sql' 'master' $script:SourceProfile '--changelog:n'
        if ($r.ExitCode -eq 0) { throw 'missing script should exit non-zero' }
        if ($r.StdOut -notmatch 'not found' -and $r.StdErr -notmatch 'not found') {
            throw "expected 'not found' diagnostic"
        }
    }
    Test-Case 'runsql.error_bad_profile' {
        $r = Invoke-Cli runsql $sql 'master' 'NO_SUCH_PROFILE_XYZ' '--changelog:n'
        if ($r.ExitCode -eq 0) { throw 'unknown profile should exit non-zero' }
    }

    # ===== runcreate =====
    # NB: Inside a create file, `runsql` lines must use `$ir>script.sql` syntax.
    # `$ir` is replaced by profile.IRPath at parse time; the `>` becomes the
    # OS path separator. Without `$ir`, the Command stays at the create-file
    # path and the dispatched runsql runs the create file as SQL (chaos).
    # `select1.sql` lives at the scratch root; `$ir` IS the scratch root.
    $createFile = Join-Path $script:Scratch 'mini.create'
    @(
        '# Test create file for headless suite',
        'runsql $ir>select1.sql -Dmaster'
    ) | Set-Content $createFile -Encoding ASCII

    # runcreate uses TEST_LOCAL (non-raw, sql-source = scratch dir) so the
    # `$ir` placeholder in create files resolves to a real directory.
    Test-Case 'runcreate.basic' {
        $r = Invoke-Cli runcreate $createFile $script:TestProfile
        Assert-ExitCode $r
        if ($r.StdOut -notmatch 'started')   { throw "expected 'started' marker. stdout: $($r.StdOut)" }
        if ($r.StdOut -notmatch 'total time') { throw "expected 'total time' marker. stdout: $($r.StdOut)" }
        if ($r.StdOut -notmatch 'return status = 0') { throw "dispatched runsql should produce 'return status = 0'" }
    }
    Test-Case 'runcreate.outfile' {
        $out = Join-Path $script:Scratch 'rc-log'
        Remove-Item "$out.out","$out.err" -Force -ErrorAction SilentlyContinue
        $r = Invoke-Cli runcreate $createFile $script:TestProfile $out
        Assert-ExitCode $r
        if (-not (Test-Path "$out.out")) { throw ".out file not created at $out.out" }
        $content = Get-Content "$out.out" -Raw
        if ($content -notmatch 'return status = 0') { throw ".out file lacks 'return status = 0'" }
    }
    Test-Case 'runcreate.platform_lines' {
        $platCreate = Join-Path $script:Scratch 'platform.create'
        @(
            '# platform lines test',
            '#NT runsql $ir>select1.sql -Dmaster',
            '#UNIX runsql $ir>select1.sql -Dmaster'
        ) | Set-Content $platCreate -Encoding ASCII
        $r = Invoke-Cli runcreate $platCreate $script:TestProfile
        Assert-ExitCode $r
        $runs = ([regex]::Matches($r.StdOut, 'Running:')).Count
        if ($runs -ne 1) { throw "expected exactly 1 #NT dispatch (got $runs). stdout: $($r.StdOut)" }
        if ($r.StdOut -notmatch 'return status = 0') { throw "the #NT dispatch should produce a successful run" }
    }
    Skip-Case 'runcreate.platform_lines_pg' 'the #NT/#UNIX prefixes here are OS dispatch lines, not DB platform (-MSSQL/-PG); no PG-specific create-file directive exists to mirror'
    Test-Case 'runcreate.dispatch_unknown_verb' {
        $unkCreate = Join-Path $script:Scratch 'unknown.create'
        @(
            '# unknown verb test',
            'some_unknown_verb foo -Dmaster'
        ) | Set-Content $unkCreate -Encoding ASCII
        $r = Invoke-Cli runcreate $unkCreate $script:TestProfile
        Assert-ExitCode $r
    }
    Test-Case 'runcreate.error_missing_file' {
        $r = Invoke-Cli runcreate 'no-such-create-file.txt' $script:SourceProfile
        if ($r.ExitCode -eq 0) { throw 'missing create file should exit non-zero' }
    }
    Skip-Case 'runcreate.bg' '-bg is a wrapper concept (background launch helper), not a source-level flag'

    # ===== i_run_upgrade =====
    # Author a minimal upgrade script. The filename embeds the upgrade number.
    $upgScript = Join-Path $script:Scratch 'sct_07.95.99001.sql'
    @(
        '# Minimal upgrade for smoke testing.',
        'SELECT 1',
        'go'
    ) | Set-Content $upgScript -Encoding ASCII

    Test-Case 'iupgrade.smoke' {
        # i_run_upgrade arg order is <database> <server> <upgrade_no> <script>
        # (4-arg form). SRM_LOCAL is raw mode; the binary refuses raw mode with
        # a deterministic error. Either path is an agent-observable outcome.
        $r = Invoke-Cli i_run_upgrade 'master' $script:SourceProfile '07.95.99001' $upgScript
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'i_run_upgrade requires option file processing|Upgrade script:|ba_upgrades_check|raw mode') {
            throw "i_run_upgrade did not produce a recognized diagnostic. output: $combined"
        }
    }
    Test-Case 'iupgrade.error_missing_args' {
        # PS5 Start-Process rejects empty -ArgumentList; pass a single bogus arg.
        $r = Invoke-Cli i_run_upgrade 'bogus'
        if ($r.ExitCode -eq 0) { throw 'i_run_upgrade with insufficient args should fail' }
    }
}

function Test-SetupCompile {
    Write-Host "`n--- 3. Setup compile commands ---" -ForegroundColor Cyan

    # ------------------------------------------------------------------
    # set_table_locations / set_required_fields / set_actions
    # Shared shape: Y/N edit prompt(s) + Y/N compile prompt. Test every
    # outcome row from feature-map.md §3, including --compile smoke tests
    # that prove the agent reaches the DB codepath against SRM_LOCAL.
    # ------------------------------------------------------------------

    $setup = Join-Path $script:Scratch 'CSS\Setup'

    # Helper: run with the fake editor + assert it was invoked on the expected file
    function Assert-EditorInvoked {
        param([Parameter(Mandatory)][string]$ExpectedFile)
        if (-not (Test-Path $script:EditorMarker)) {
            throw "editor marker missing - editor was not invoked"
        }
        $content = Get-Content $script:EditorMarker -Raw
        if ($content -notmatch [regex]::Escape((Split-Path -Leaf $ExpectedFile))) {
            throw "editor marker did not record expected file '$ExpectedFile'. Marker: $content"
        }
    }
    function Reset-EditorMarker { Remove-Item $script:EditorMarker -Force -ErrorAction SilentlyContinue }

    # ===== set_table_locations =====
    Test-Case 'set_table_locations.edit_header' {
        Reset-EditorMarker
        $r = Invoke-Cli set_table_locations $script:TestProfile '--edit-header' '--no-compile'
        Assert-ExitCode $r
        Assert-EditorInvoked 'table_locations'
    }
    Test-Case 'set_table_locations.no_edit_header' {
        Assert-ExitCode (Invoke-Cli set_table_locations $script:TestProfile '--no-edit-header' '--no-compile')
    }
    Test-Case 'set_table_locations.skip_edit' {
        Assert-ExitCode (Invoke-Cli set_table_locations $script:TestProfile '--skip-edit' '--no-compile')
    }
    Test-Case 'set_table_locations.no_compile' {
        $r = Invoke-Cli set_table_locations $script:TestProfile '--skip-edit' '--no-compile'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch 'Compiling table_locations|Cancelled') {
            # Expected path is "Cancelled." after --no-compile
            if ($r.StdOut -match 'compile_table_locations started') {
                throw '--no-compile still reached the compile path'
            }
        }
    }
    Test-Case 'set_table_locations.compile_smoke' {
        # --compile drives the DB codepath. Atlas/SRM has no `ibs..table_locations`
        # table; we verify the codepath was reached, not full success.
        $r = Invoke-Cli set_table_locations $script:TestProfile '--skip-edit' '--compile'
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Compiling table_locations|compile_table_locations started|table_locations insert') {
            throw "--compile codepath not reached. stdout: $($r.StdOut)"
        }
    }
    Test-Case 'set_table_locations.error_source_missing' {
        # Move the source file aside; CLI must error cleanly.
        $src = Join-Path $setup 'table_locations'
        $bak = "$src.bak"
        Move-Item $src $bak -Force
        try {
            $r = Invoke-Cli set_table_locations $script:TestProfile '--skip-edit' '--no-compile'
            if ($r.ExitCode -eq 0) { throw 'missing source file should exit non-zero' }
            $combined = "$($r.StdOut)`n$($r.StdErr)"
            if ($combined -notmatch 'table_locations file not found|not found') {
                throw "expected 'not found' diagnostic. output: $combined"
            }
        } finally {
            Move-Item $bak $src -Force
        }
    }
    Test-Case 'set_table_locations.error_profile_missing' {
        $r = Invoke-Cli set_table_locations 'NO_SUCH_PROFILE_XYZ' '--skip-edit' '--no-compile'
        if ($r.ExitCode -eq 0) { throw 'unknown profile should exit non-zero' }
    }

    # ===== set_required_fields =====
    Test-Case 'set_required_fields.edit_header' {
        Reset-EditorMarker
        $r = Invoke-Cli set_required_fields $script:TestProfile '--edit-header' '--no-edit-detail' '--no-compile'
        Assert-ExitCode $r
        Assert-EditorInvoked 'css.required_fields'
    }
    Test-Case 'set_required_fields.edit_detail' {
        Reset-EditorMarker
        $r = Invoke-Cli set_required_fields $script:TestProfile '--no-edit-header' '--edit-detail' '--no-compile'
        Assert-ExitCode $r
        Assert-EditorInvoked 'css.required_fields_dtl'
    }
    Test-Case 'set_required_fields.no_edit_header' {
        Assert-ExitCode (Invoke-Cli set_required_fields $script:TestProfile '--no-edit-header' '--no-edit-detail' '--no-compile')
    }
    Test-Case 'set_required_fields.no_edit_detail' {
        Assert-ExitCode (Invoke-Cli set_required_fields $script:TestProfile '--no-edit-header' '--no-edit-detail' '--no-compile')
    }
    Test-Case 'set_required_fields.skip_edit' {
        Assert-ExitCode (Invoke-Cli set_required_fields $script:TestProfile '--skip-edit' '--no-compile')
    }
    Test-Case 'set_required_fields.no_compile' {
        $r = Invoke-Cli set_required_fields $script:TestProfile '--skip-edit' '--no-compile'
        Assert-ExitCode $r
        if ($r.StdOut -match 'Starting compile_required_fields') {
            throw '--no-compile still reached the compile path'
        }
    }
    Test-Case 'set_required_fields.compile_smoke' {
        $r = Invoke-Cli set_required_fields $script:TestProfile '--skip-edit' '--compile'
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Compiling required fields|Starting compile_required_fields|backup files for existing required fields') {
            throw "--compile codepath not reached. stdout: $($r.StdOut)"
        }
    }
    Test-Case 'set_required_fields.error_header_missing' {
        $src = Join-Path $setup 'css.required_fields'
        $bak = "$src.bak"
        Move-Item $src $bak -Force
        try {
            $r = Invoke-Cli set_required_fields $script:TestProfile '--skip-edit' '--no-compile'
            if ($r.ExitCode -eq 0) { throw 'missing required_fields header should exit non-zero' }
            $combined = "$($r.StdOut)`n$($r.StdErr)"
            if ($combined -notmatch 'required_fields file not found|not found') {
                throw "expected 'not found' diagnostic. output: $combined"
            }
        } finally {
            Move-Item $bak $src -Force
        }
    }
    Test-Case 'set_required_fields.error_detail_missing' {
        $src = Join-Path $setup 'css.required_fields_dtl'
        $bak = "$src.bak"
        Move-Item $src $bak -Force
        try {
            $r = Invoke-Cli set_required_fields $script:TestProfile '--skip-edit' '--no-compile'
            if ($r.ExitCode -eq 0) { throw 'missing required_fields_dtl should exit non-zero' }
        } finally {
            Move-Item $bak $src -Force
        }
    }
    Test-Case 'set_required_fields.error_profile_missing' {
        $r = Invoke-Cli set_required_fields 'NO_SUCH_PROFILE_XYZ' '--skip-edit' '--no-compile'
        if ($r.ExitCode -eq 0) { throw 'unknown profile should exit non-zero' }
    }

    # ===== set_actions =====
    Test-Case 'set_actions.edit_header' {
        Reset-EditorMarker
        $r = Invoke-Cli set_actions $script:TestProfile '--edit-header' '--no-edit-detail' '--no-compile'
        Assert-ExitCode $r
        Assert-EditorInvoked 'actions'
    }
    Test-Case 'set_actions.edit_detail' {
        Reset-EditorMarker
        $r = Invoke-Cli set_actions $script:TestProfile '--no-edit-header' '--edit-detail' '--no-compile'
        Assert-ExitCode $r
        Assert-EditorInvoked 'actions_dtl'
    }
    Test-Case 'set_actions.no_edit_header' {
        Assert-ExitCode (Invoke-Cli set_actions $script:TestProfile '--no-edit-header' '--no-edit-detail' '--no-compile')
    }
    Test-Case 'set_actions.no_edit_detail' {
        Assert-ExitCode (Invoke-Cli set_actions $script:TestProfile '--no-edit-header' '--no-edit-detail' '--no-compile')
    }
    Test-Case 'set_actions.skip_edit' {
        Assert-ExitCode (Invoke-Cli set_actions $script:TestProfile '--skip-edit' '--no-compile')
    }
    Test-Case 'set_actions.no_compile' {
        $r = Invoke-Cli set_actions $script:TestProfile '--skip-edit' '--no-compile'
        Assert-ExitCode $r
        if ($r.StdOut -match 'Starting compile_actions') {
            throw '--no-compile still reached the compile path'
        }
        if ($r.StdOut -notmatch 'Finished') { throw "expected 'Finished.' in output. stdout: $($r.StdOut)" }
    }
    Test-Case 'set_actions.compile_smoke' {
        $r = Invoke-Cli set_actions $script:TestProfile '--skip-edit' '--compile'
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Compiling actions|Starting compile_actions|Line Extraction of actions') {
            throw "--compile codepath not reached. stdout: $($r.StdOut)"
        }
    }
    Test-Case 'set_actions.error_header_missing' {
        $src = Join-Path $setup 'actions'
        $bak = "$src.bak"
        Move-Item $src $bak -Force
        try {
            $r = Invoke-Cli set_actions $script:TestProfile '--skip-edit' '--no-compile'
            if ($r.ExitCode -eq 0) { throw 'missing actions header should exit non-zero' }
            $combined = "$($r.StdOut)`n$($r.StdErr)"
            if ($combined -notmatch 'actions file not found|not found') {
                throw "expected 'not found' diagnostic. output: $combined"
            }
        } finally {
            Move-Item $bak $src -Force
        }
    }
    Test-Case 'set_actions.error_detail_missing' {
        $src = Join-Path $setup 'actions_dtl'
        $bak = "$src.bak"
        Move-Item $src $bak -Force
        try {
            $r = Invoke-Cli set_actions $script:TestProfile '--skip-edit' '--no-compile'
            if ($r.ExitCode -eq 0) { throw 'missing actions_dtl should exit non-zero' }
        } finally {
            Move-Item $bak $src -Force
        }
    }
    Test-Case 'set_actions.error_profile_missing' {
        $r = Invoke-Cli set_actions 'NO_SUCH_PROFILE_XYZ' '--skip-edit' '--no-compile'
        if ($r.ExitCode -eq 0) { throw 'unknown profile should exit non-zero' }
    }

    # set_options — file mutations are deterministic; verify by reading back.
    $defFile     = Join-Path $script:Scratch 'CSS\Setup\options.def'
    $companyFile = Join-Path $script:Scratch 'CSS\Setup\options.101'

    # NB: PS5 Start-Process -ArgumentList splits args containing spaces; the
    # trailing positional becomes the "server name" via compile_variables.
    # Keep every flag VALUE space-free in these tests.
    Test-Case 'set_options.add_value' {
        @('v:testopt <<defaultvalue>> Test', 'c:testflag - Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile `
            '--add' 'newopt' '--type' 'value' '--dynamic' '--default' 'newval' `
            '--description' 'headless-test' `
            '--mod-num' '07.95.99001' '--mod-name' 'TESTAGENT' '--mod-reason' 'headless-suite'
        Assert-ExitCode $r
        Assert-FileContains $defFile 'V:newopt'
        Assert-FileContains $defFile '<<newval>>'
        Assert-FileContains $defFile 'headless-test'
        Assert-FileContains $defFile '# 07.95.99001 -->'
        Assert-FileContains $defFile '# 07.95.99001 <--'
        Assert-FileContains $defFile 'CHG '
        Assert-FileContains $defFile 'TESTAGENT'
        Assert-FileContains $defFile 'headless-suite'
    }
    Test-Case 'set_options.add_onoff_off' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile `
            '--add' 'offflag' '--type' 'onoff' '--dynamic' '--state' 'off' `
            '--mod-num' '07.95.99020' '--mod-name' 'TESTAGENT' '--mod-reason' 'off-test'
        Assert-ExitCode $r
        Assert-FileContains $defFile 'C:offflag - No description'
    }
    Test-Case 'set_options.customize_value' {
        @('v:testopt <<v>> Test', 'v:tweak <<original>> To-customize') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<v>> Test') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--merge-company' `
            '--customize' 'tweak=overridden' `
            '--mod-num' '07.95.99005' '--mod-name' 'TESTAGENT' '--mod-reason' 'customize-test'
        Assert-ExitCode $r
        Assert-FileContains $companyFile '<<overridden>>'
        if ((Get-Content $companyFile -Raw) -match '<<original>>') {
            throw 'merge kept the original value; --customize was ignored'
        }
    }
    Test-Case 'set_options.customize_onoff' {
        @('v:testopt <<v>> Test', 'c:tweakflag - To-customize') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<v>> Test') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--merge-company' `
            '--customize' 'tweakflag=on' `
            '--mod-num' '07.95.99006' '--mod-name' 'TESTAGENT' '--mod-reason' 'customize-onoff'
        Assert-ExitCode $r
        Assert-FileContains $companyFile 'c:tweakflag +'
    }
    Test-Case 'set_options.add_onoff' {
        @('v:testopt <<defaultvalue>> Test value option') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile `
            '--add' 'newflag' '--type' 'onoff' '--static' '--state' 'on' `
            '--mod-num' '07.95.99002' '--mod-name' 'TESTAGENT' '--mod-reason' 'headless-suite'
        Assert-ExitCode $r
        Assert-FileContains $defFile 'c:newflag + No description'
    }
    Test-Case 'set_options.merge_company' {
        @('v:testopt <<defaultvalue>> Test option', 'v:extra <<val>> Extra') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<companyvalue>> Test option') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--merge-company' `
            '--mod-num' '07.95.99003' '--mod-name' 'TESTAGENT' '--mod-reason' 'merge-test'
        Assert-ExitCode $r
        Assert-FileContains $companyFile 'v:extra'
    }
    Test-Case 'set_options.merge_profile' {
        $profileFile = Join-Path $script:Scratch "CSS\Setup\options.101.$script:TestProfile"
        @('v:testopt <<defaultvalue>> Test') | Set-Content $profileFile -Encoding UTF8
        @('v:testopt <<defaultvalue>> Test', 'v:added <<x>> Added') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--merge-profile' `
            '--mod-num' '07.95.99004' '--mod-name' 'TESTAGENT' '--mod-reason' 'merge-test'
        Assert-ExitCode $r
        Assert-FileContains $profileFile 'v:added'
    }
    Test-Case 'set_options.sync_all_adds' {
        @('v:testopt <<defaultvalue>> A', 'v:onlydef <<x>> Only in def') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<defaultvalue>> A') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--sync' '--all-adds'
        Assert-ExitCode $r
        Assert-FileContains $companyFile 'v:onlydef'
    }
    Test-Case 'set_options.sync_add_only' {
        @('v:testopt <<v>> A', 'v:wanted <<x>> Wanted', 'v:unwanted <<y>> Unwanted') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<v>> A') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--sync' '--add-only' 'wanted'
        Assert-ExitCode $r
        Assert-FileContains $companyFile 'v:wanted'
        if ((Get-Content $companyFile -Raw) -match 'v:unwanted') { throw 'unwanted option leaked in' }
    }
    Test-Case 'set_options.sync_all_removes' {
        @('v:testopt <<v>> A') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<v>> A', 'v:extra <<v>> Extra') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--sync' '--all-removes'
        Assert-ExitCode $r
        if ((Get-Content $companyFile -Raw) -match 'v:extra') { throw 'extra option still present' }
    }
    Test-Case 'set_options.sync_remove' {
        @('v:testopt <<v>> A') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<v>> A', 'v:dropme <<v>> Drop', 'v:keepme <<v>> Keep') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--sync' '--remove' 'dropme'
        Assert-ExitCode $r
        if ((Get-Content $companyFile -Raw) -match 'v:dropme') { throw 'dropme still present' }
        Assert-FileContains $companyFile 'v:keepme'
    }
    Test-Case 'set_options.copy' {
        @('v:testopt <<v>> A') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--copy' 'options.101' '--to' 'options.202'
        Assert-ExitCode $r
        $copied = Join-Path $script:Scratch 'CSS\Setup\options.202'
        if (-not (Test-Path $copied)) { throw 'copy target not created' }
        Remove-Item $copied -Force -ErrorAction SilentlyContinue
    }
    # --import drives the DB codepath. SRM_LOCAL is Atlas/SRM (no SBN procs);
    # the agent should still reach the import codepath deterministically.
    Test-Case 'set_options.import_smoke' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<v>> Test') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--import'
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Compiling options|Import of options started|Starting options insert|i_import_options') {
            throw "no recognized --import progress marker. stdout: $($r.StdOut) stderr: $($r.StdErr)"
        }
    }
    Test-Case 'set_options.add_then_import_smoke' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<v>> Test') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile `
            '--add' 'addimp' '--type' 'value' '--dynamic' '--default' 'v' `
            '--mod-num' '07.95.99007' '--mod-name' 'TESTAGENT' '--mod-reason' 'add-then-import' `
            '--import'
        Assert-FileContains $defFile 'V:addimp'
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Compiling options|Import of options started') {
            throw "add+import: import codepath not reached. stdout: $($r.StdOut)"
        }
    }

    # ---- Error-path tests (every error an agent might hit) ----

    Test-Case 'set_options.error_add_missing_type' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'x' '--dynamic' '--default' 'v' `
            '--mod-num' '07.95.99008' '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw '--add without --type should fail' }
        if ($r.StdErr -notmatch '--type') { throw "stderr should mention --type: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_add_value_missing_default' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'x' '--type' 'value' '--dynamic' `
            '--mod-num' '07.95.99009' '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw '--type value without --default should fail' }
        if ($r.StdErr -notmatch '--default') { throw "stderr should mention --default: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_add_onoff_missing_state' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'x' '--type' 'onoff' '--dynamic' `
            '--mod-num' '07.95.99010' '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw '--type onoff without --state should fail' }
        if ($r.StdErr -notmatch '--state') { throw "stderr should mention --state: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_add_both_static_and_dynamic' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'x' '--type' 'value' '--static' '--dynamic' '--default' 'v' `
            '--mod-num' '07.95.99011' '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw 'both --static and --dynamic should be rejected' }
        if ($r.StdErr -notmatch 'mutually exclusive') { throw "stderr should mention mutual exclusion: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_add_neither_static_nor_dynamic' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'x' '--type' 'value' '--default' 'v' `
            '--mod-num' '07.95.99012' '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw 'neither --static nor --dynamic should be rejected' }
        if ($r.StdErr -notmatch '--static') { throw "stderr should mention --static: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_add_name_too_long' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'toolongname' '--type' 'value' '--dynamic' '--default' 'v' `
            '--mod-num' '07.95.99013' '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw 'name >8 chars should be rejected' }
        if ($r.StdErr -notmatch '8 characters') { throw "stderr should mention 8-char limit: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_add_duplicate_name' {
        @('v:dup <<v>> existing') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'dup' '--type' 'value' '--dynamic' '--default' 'v' `
            '--mod-num' '07.95.99014' '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw 'duplicate option name should be rejected' }
        if ($r.StdErr -notmatch 'already exists') { throw "stderr should say 'already exists': $($r.StdErr)" }
    }
    Test-Case 'set_options.error_add_missing_modnum' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'x' '--type' 'value' '--dynamic' '--default' 'v' `
            '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw '--add without --mod-num should fail' }
        if ($r.StdErr -notmatch '--mod-num') { throw "stderr should mention --mod-num: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_add_missing_modname' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'x' '--type' 'value' '--dynamic' '--default' 'v' `
            '--mod-num' '07.95.99015' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw '--add without --mod-name should fail' }
        if ($r.StdErr -notmatch '--mod-name') { throw "stderr should mention --mod-name: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_add_missing_modreason' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--add' 'x' '--type' 'value' '--dynamic' '--default' 'v' `
            '--mod-num' '07.95.99016' '--mod-name' 'T'
        if ($r.ExitCode -eq 0) { throw '--add without --mod-reason should fail' }
        if ($r.StdErr -notmatch '--mod-reason') { throw "stderr should mention --mod-reason: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_copy_without_to' {
        $r = Invoke-Cli set_options $script:TestProfile '--copy' 'options.101'
        if ($r.ExitCode -eq 0) { throw '--copy without --to should fail' }
        if ($r.StdErr -notmatch '--to') { throw "stderr should mention --to: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_copy_bad_name' {
        $r = Invoke-Cli set_options $script:TestProfile '--copy' 'options.101' '--to' 'notoptions.foo'
        if ($r.ExitCode -eq 0) { throw '--to value not starting with options. should be rejected' }
        if ($r.StdErr -notmatch 'options\.') { throw "stderr should mention options. prefix: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_copy_target_exists' {
        @('v:testopt <<v>> Test') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--copy' 'options.def' '--to' 'options.101'
        if ($r.ExitCode -eq 0) { throw 'copy to existing file should be rejected' }
        if ($r.StdErr -notmatch 'already exists') { throw "stderr should say 'already exists': $($r.StdErr)" }
    }
    Test-Case 'set_options.error_sync_no_selector' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<v>> Test') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--sync'
        if ($r.ExitCode -eq 0) { throw '--sync without selector should fail' }
        if ($r.StdErr -notmatch '--all-adds|--add-only|--all-removes|--remove') {
            throw "stderr should list selector flags: $($r.StdErr)"
        }
    }
    Test-Case 'set_options.error_mutex_add_and_sync' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile `
            '--add' 'x' '--type' 'value' '--dynamic' '--default' 'v' `
            '--sync' '--all-adds' `
            '--mod-num' '07.95.99017' '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw '--add and --sync together should be rejected' }
        if ($r.StdErr -notmatch 'mutually exclusive') { throw "stderr should mention mutual exclusion: $($r.StdErr)" }
    }
    Test-Case 'set_options.error_customize_malformed' {
        @('v:testopt <<v>> Test') | Set-Content $defFile -Encoding UTF8
        @('v:testopt <<v>> Test') | Set-Content $companyFile -Encoding UTF8
        $r = Invoke-Cli set_options $script:TestProfile '--merge-company' `
            '--customize' 'no_equals_sign' `
            '--mod-num' '07.95.99018' '--mod-name' 'T' '--mod-reason' 'r'
        if ($r.ExitCode -eq 0) { throw 'malformed --customize should be rejected' }
        if ($r.StdErr -notmatch 'NAME=VALUE') { throw "stderr should show NAME=VALUE format: $($r.StdErr)" }
    }
}

function Test-Messages {
    Write-Host "`n--- 4. Messages ---" -ForegroundColor Cyan

    # extract_msg runs export-then-import; TEST_LOCAL (non-raw, scratch sql-source)
    # has no SBN message tables, so BCP OUT will fail. The agent still gets a
    # deterministic codepath reach — verify it.
    Test-Case 'extract_msg.smoke' {
        $r = Invoke-Cli extract_msg $script:TestProfile
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Exporting messages|Step 1|extract_msg|ibs_messages') {
            throw "extract_msg codepath not reached. stdout: $($r.StdOut) stderr: $($r.StdErr)"
        }
    }
    Test-Case 'extract_msg.error_bad_profile' {
        $r = Invoke-Cli extract_msg 'NO_SUCH_PROFILE_XYZ'
        if ($r.ExitCode -eq 0) { throw 'unknown profile should exit non-zero' }
    }

    # ===== set_messages headless mode =====
    # Non-GONZO tests run against TEST_LOCAL (non-raw, scratch sql-source with
    # all 10 css.<type>_msg* placeholder files seeded in Initialize-Suite).
    # The Atlas DB has no SBN message tables, so the BCP step will error - we
    # verify codepath reach via stdout markers, not full DB round-trip.

    Test-Case 'set_messages.import' {
        $r = Invoke-Cli set_messages '--import' $script:TestProfile
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Compiling messages|Starting compile_msg|Source files:|saved translations') {
            throw "--import codepath not reached. stdout: $($r.StdOut) stderr: $($r.StdErr)"
        }
    }
    Test-Case 'set_messages.import_keep_saved' {
        $r = Invoke-Cli set_messages '--import' '--on-saved' 'keep' $script:TestProfile
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        # Either the saved-translations branch fired (then 'keep' was honored),
        # or there were no saved rows. Either way: codepath reached, no prompt.
        if ($combined -notmatch 'Compiling messages|Starting compile_msg|Source files:') {
            throw "--import --on-saved keep codepath not reached. stdout: $($r.StdOut)"
        }
        # Critical: no interactive prompt text leaked out.
        if ($combined -match 'Enter choice \(1, 2, or 3\)') {
            throw 'headless --on-saved should bypass the 3-way prompt, but it appeared'
        }
    }
    Test-Case 'set_messages.import_discard_saved' {
        $r = Invoke-Cli set_messages '--import' '--on-saved' 'discard' $script:TestProfile
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Compiling messages|Starting compile_msg|Source files:') {
            throw "--import --on-saved discard codepath not reached. stdout: $($r.StdOut)"
        }
        if ($combined -match 'Enter choice \(1, 2, or 3\)') {
            throw 'headless --on-saved should bypass the 3-way prompt'
        }
    }
    Test-Case 'set_messages.import_cancel_saved' {
        # --on-saved cancel: if saved rows exist, exits without changes.
        # On TEST_LOCAL (no gui_messages_save table), there are no saved rows
        # so the cancel branch never fires - the import proceeds normally.
        # Either way: no prompt, deterministic exit.
        $r = Invoke-Cli set_messages '--import' '--on-saved' 'cancel' $script:TestProfile
        if ($r.StdOut -match 'Enter choice \(1, 2, or 3\)') {
            throw 'headless --on-saved should bypass the 3-way prompt'
        }
    }
    Test-Case 'set_messages.export' {
        $r = Invoke-Cli set_messages '--export' '--yes' $script:TestProfile
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Exporting messages|ibs_messages|extract|BCP') {
            throw "--export codepath not reached. stdout: $($r.StdOut) stderr: $($r.StdErr)"
        }
        # Critical: --yes bypasses the 'Are you sure?' prompt.
        if ($r.StdOut -match 'Are you sure') {
            throw '--yes should bypass the export confirmation prompt'
        }
    }
    Test-Case 'set_messages.gonzo_import_blocked' {
        # GONZO --import must be rejected BEFORE any DB call. Uses real GONZO
        # profile (name-based detection); no Sybase connection happens because
        # the rejection fires at the headless-dispatch validation step.
        $r = Invoke-Cli set_messages '--import' 'GONZO'
        if ($r.ExitCode -eq 0) { throw '--import against GONZO must be rejected' }
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'GONZO|not allowed|canonical') {
            throw "stderr should explain GONZO rejection. output: $combined"
        }
    }
    Skip-Case 'set_messages.gonzo_export' 'cannot run safely - would BCP-OUT all message tables from real GONZO and overwrite the canonical CSS/Setup/css.*_msg* files on disk; manual verification only'

    # Bonus error-path tests (every validation surface in RunSetMessagesHeadless)
    Test-Case 'set_messages.error_mutex' {
        $r = Invoke-Cli set_messages '--import' '--export' $script:TestProfile
        if ($r.ExitCode -eq 0) { throw '--import + --export together must be rejected' }
        if ($r.StdErr -notmatch 'mutually exclusive') { throw "stderr should mention mutual exclusion: $($r.StdErr)" }
    }
    Test-Case 'set_messages.error_no_primary_action' {
        # --on-saved alone (no --import / --export) is a misuse. The headless
        # dispatcher catches it cleanly instead of falling through to the
        # interactive menu (which would hang on stdin).
        $r = Invoke-Cli set_messages '--on-saved' 'keep' $script:TestProfile
        if ($r.ExitCode -eq 0) { throw 'meaningless --on-saved (no primary action) must be rejected' }
        if ($r.StdErr -notmatch '--import|--export') { throw "stderr should list valid primary actions: $($r.StdErr)" }
    }
    Test-Case 'set_messages.error_export_without_yes' {
        $r = Invoke-Cli set_messages '--export' $script:TestProfile
        if ($r.ExitCode -eq 0) { throw 'non-GONZO --export without --yes must be rejected' }
        if ($r.StdErr -notmatch '--yes') { throw "stderr should mention --yes: $($r.StdErr)" }
    }
    Test-Case 'set_messages.error_bad_on_saved' {
        $r = Invoke-Cli set_messages '--import' '--on-saved' 'bogus' $script:TestProfile
        if ($r.ExitCode -eq 0) { throw '--on-saved bogus must be rejected' }
        if ($r.StdErr -notmatch 'keep|discard|cancel') { throw "stderr should list valid values: $($r.StdErr)" }
    }
    Test-Case 'set_messages.error_bad_profile' {
        $r = Invoke-Cli set_messages '--import' 'NO_SUCH_PROFILE_XYZ'
        if ($r.ExitCode -eq 0) { throw 'unknown profile must exit non-zero' }
    }

    # compile_msg.exe accepts the same flags (both binaries route to RunSetMessages).
    Test-Case 'compile_msg.import_smoke' {
        $r = Invoke-Cli compile_msg '--import' $script:TestProfile
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        if ($combined -notmatch 'Compiling messages|Starting compile_msg|Source files:') {
            throw "compile_msg --import codepath not reached. stdout: $($r.StdOut)"
        }
    }
    Test-Case 'compile_msg.gonzo_import_blocked' {
        $r = Invoke-Cli compile_msg '--import' 'GONZO'
        if ($r.ExitCode -eq 0) { throw 'compile_msg --import GONZO must be rejected' }
    }
}

function Test-ProfileManagement {
    Write-Host "`n--- 5. Profile management ---" -ForegroundColor Cyan

    $scratchProfile = 'TEST_PROFILE_SUITE'
    # Defensive: in case a prior run left these behind
    foreach ($n in @($scratchProfile, "${scratchProfile}_2", "${scratchProfile}_RAW", "${scratchProfile}_PG", "${scratchProfile}_PG2")) {
        Invoke-Cli set_profile '--delete' $n '--yes' | Out-Null
    }

    # --- Happy-path create / view / edit / copy / delete with real assertions ---
    Test-Case 'set_profile.create_mssql' {
        $r = Invoke-Cli set_profile '--create' $scratchProfile `
            '--platform' 'mssql' '--host' '127.0.0.1' '--port' '1433' `
            '--user' 'sa' '--password' 'placeholder' `
            '--company' '101' '--sql-source' $script:Scratch `
            '--alias' 'TPS,TPSUITE'
        Assert-ExitCode $r
        Assert-ProfileExists $scratchProfile
        $p = Get-Profile $scratchProfile
        # JSON uses SCREAMING_SNAKE_CASE — use those property names directly.
        Assert-Field $p 'PLATFORM' 'MSSQL'
        Assert-Field $p 'HOST'     '127.0.0.1'
        Assert-Field $p 'PORT'     1433
        Assert-Field $p 'USERNAME' 'sa'
        Assert-Field $p 'COMPANY'  '101'
        if ($p.ALIASES.Count -ne 2 -or $p.ALIASES -notcontains 'TPS' -or $p.ALIASES -notcontains 'TPSUITE') {
            throw "aliases not persisted correctly: $($p.ALIASES -join ',')"
        }
    }

    Test-Case 'set_profile.create_pg' {
        $r = Invoke-Cli set_profile '--create' "${scratchProfile}_PG" `
            '--platform' 'pg' '--host' '127.0.0.1' `
            '--user' 'postgres' '--password' 'placeholder' `
            '--company' '101' '--sql-source' $script:Scratch
        Assert-ExitCode $r
        Assert-ProfileExists "${scratchProfile}_PG"
        $p = Get-Profile "${scratchProfile}_PG"
        # '--platform pg' must canonicalize to PLATFORM=POSTGRES and default PORT=5432
        Assert-Field $p 'PLATFORM' 'POSTGRES'
        Assert-Field $p 'PORT'     5432
        $viewOut = Invoke-Cli set_profile '--view' "${scratchProfile}_PG"
        Assert-ExitCode $viewOut
        if ($viewOut.StdOut -notmatch '5432') { throw "view output missing default PG port. stdout: $($viewOut.StdOut)" }
        Invoke-Cli set_profile '--delete' "${scratchProfile}_PG" '--yes' | Out-Null
    }
    Test-Case 'set_profile.create_pg_platform_alias' {
        # '--platform postgres' (the long form) must also be accepted
        $r = Invoke-Cli set_profile '--create' "${scratchProfile}_PG2" `
            '--platform' 'postgres' '--host' '127.0.0.1' `
            '--user' 'postgres' '--password' 'placeholder' `
            '--company' '101' '--sql-source' $script:Scratch
        Assert-ExitCode $r
        Assert-Field (Get-Profile "${scratchProfile}_PG2") 'PLATFORM' 'POSTGRES'
        Invoke-Cli set_profile '--delete' "${scratchProfile}_PG2" '--yes' | Out-Null
    }

    Test-Case 'set_profile.view' {
        $r = Invoke-Cli set_profile '--view' $scratchProfile
        Assert-ExitCode $r
        if ($r.StdOut -notmatch $scratchProfile) { throw "view output missing profile name" }
        if ($r.StdOut -notmatch '127\.0\.0\.1') { throw "view output missing host" }
        if ($r.StdOut -notmatch '1433')        { throw "view output missing port" }
    }

    Test-Case 'set_profile.edit_port' {
        Assert-ExitCode (Invoke-Cli set_profile '--edit' $scratchProfile '--port' '1434')
        Assert-Field (Get-Profile $scratchProfile) 'PORT' 1434
    }

    Test-Case 'set_profile.edit_clear_aliases' {
        Assert-ExitCode (Invoke-Cli set_profile '--edit' $scratchProfile '--no-aliases')
        $p = Get-Profile $scratchProfile
        if ($null -ne $p.ALIASES -and $p.ALIASES.Count -gt 0) {
            throw "aliases not cleared: $($p.ALIASES -join ',')"
        }
    }

    Test-Case 'set_profile.copy' {
        $r = Invoke-Cli set_profile '--copy' $scratchProfile '--to' "${scratchProfile}_2"
        Assert-ExitCode $r
        Assert-ProfileExists "${scratchProfile}_2"
        $copied = Get-Profile "${scratchProfile}_2"
        if ($null -ne $copied.ALIASES -and $copied.ALIASES.Count -gt 0) {
            throw "copied profile retained aliases (should be cleared)"
        }
        Assert-Field $copied 'HOST' '127.0.0.1'
        Assert-Field $copied 'PORT' 1434
        Invoke-Cli set_profile '--delete' "${scratchProfile}_2" '--yes' | Out-Null
    }

    Test-Case 'set_profile.create_raw' {
        $r = Invoke-Cli set_profile '--create' "${scratchProfile}_RAW" `
            '--platform' 'mssql' '--host' '127.0.0.1' `
            '--user' 'sa' '--password' 'p' '--raw'
        Assert-ExitCode $r
        $p = Get-Profile "${scratchProfile}_RAW"
        Assert-Field $p 'RAW_MODE' $true
        Assert-Field $p 'COMPANY'  '0'
        if (-not [string]::IsNullOrEmpty($p.SQL_SOURCE)) { throw "raw profile should clear SQL_SOURCE (got '$($p.SQL_SOURCE)')" }
        Invoke-Cli set_profile '--delete' "${scratchProfile}_RAW" '--yes' | Out-Null
    }

    Test-Case 'set_profile.delete_without_yes_errors' {
        $r = Invoke-Cli set_profile '--delete' $scratchProfile
        if ($r.ExitCode -eq 0) { throw 'expected non-zero exit when --yes is missing' }
        Assert-ProfileExists $scratchProfile  # must NOT have been deleted
    }

    Test-Case 'set_profile.delete_yes' {
        Assert-ExitCode (Invoke-Cli set_profile '--delete' $scratchProfile '--yes')
        Assert-ProfileAbsent $scratchProfile
    }

    # --- Error-path tests (every error an agent might hit) ---
    Test-Case 'set_profile.error_create_missing_host' {
        $r = Invoke-Cli set_profile '--create' 'X1' '--platform' 'mssql' '--user' 'u' '--password' 'p' '--raw'
        if ($r.ExitCode -eq 0) { throw 'create without --host should fail' }
        if ($r.StdErr -notmatch '--host') { throw "stderr should mention --host: $($r.StdErr)" }
    }
    Test-Case 'set_profile.error_create_bad_platform' {
        $r = Invoke-Cli set_profile '--create' 'X2' '--platform' 'oracle' '--host' 'h' '--user' 'u' '--password' 'p' '--raw'
        if ($r.ExitCode -eq 0) { throw 'create with bad platform should fail' }
        if ($r.StdErr -notmatch 'mssql.*sybase.*postgres') { throw "stderr should list valid platforms incl. postgres: $($r.StdErr)" }
    }
    Test-Case 'set_profile.error_create_reserved_name' {
        $r = Invoke-Cli set_profile '--create' 'VERSION' '--platform' 'mssql' '--host' 'h' '--user' 'u' '--password' 'p' '--raw'
        if ($r.ExitCode -eq 0) { throw 'reserved name should be rejected' }
        if ($r.StdErr -notmatch 'reserved') { throw "stderr should say 'reserved': $($r.StdErr)" }
    }
    Test-Case 'set_profile.error_create_invalid_name' {
        # Use a name with an invalid character that doesn't require shell quoting
        # (PS5 Start-Process -ArgumentList splits args containing spaces).
        $r = Invoke-Cli set_profile '--create' 'BAD@NAME' '--platform' 'mssql' '--host' 'h' '--user' 'u' '--password' 'p' '--raw'
        if ($r.ExitCode -eq 0) {
            Invoke-Cli set_profile '--delete' 'BAD@NAME' '--yes' | Out-Null
            throw 'invalid-char name should be rejected'
        }
    }
    Test-Case 'set_profile.error_create_duplicate' {
        # SRM_LOCAL already exists in settings
        $r = Invoke-Cli set_profile '--create' $script:SourceProfile '--platform' 'mssql' '--host' 'h' '--user' 'u' '--password' 'p' '--raw'
        if ($r.ExitCode -eq 0) { throw 'duplicate name should be rejected' }
        if ($r.StdErr -notmatch 'already exists') { throw "stderr should say 'already exists': $($r.StdErr)" }
    }
    Test-Case 'set_profile.error_edit_nonexistent' {
        $r = Invoke-Cli set_profile '--edit' 'NO_SUCH_PROFILE_XYZ' '--port' '1234'
        if ($r.ExitCode -eq 0) { throw 'edit of missing profile should fail' }
    }
    Test-Case 'set_profile.error_view_nonexistent' {
        $r = Invoke-Cli set_profile '--view' 'NO_SUCH_PROFILE_XYZ'
        if ($r.ExitCode -eq 0) { throw 'view of missing profile should fail' }
    }
    Test-Case 'set_profile.error_delete_nonexistent' {
        $r = Invoke-Cli set_profile '--delete' 'NO_SUCH_PROFILE_XYZ' '--yes'
        if ($r.ExitCode -eq 0) { throw 'delete of missing profile should fail' }
    }
    Test-Case 'set_profile.error_copy_without_to' {
        $r = Invoke-Cli set_profile '--copy' $script:SourceProfile
        if ($r.ExitCode -eq 0) { throw 'copy without --to should fail' }
        if ($r.StdErr -notmatch '--to') { throw "stderr should mention --to: $($r.StdErr)" }
    }
    Test-Case 'set_profile.error_alias_collision' {
        # Try to create a profile aliased to an existing profile name
        $r = Invoke-Cli set_profile '--create' 'ALIASTEST' '--platform' 'mssql' '--host' 'h' '--user' 'u' '--password' 'p' `
                                    '--raw' '--alias' $script:SourceProfile
        if ($r.ExitCode -eq 0) {
            Invoke-Cli set_profile '--delete' 'ALIASTEST' '--yes' | Out-Null
            throw 'alias matching another profile name should be rejected'
        }
    }
    Test-Case 'set_profile.error_test_without_what' {
        $r = Invoke-Cli set_profile '--test' $script:TestProfile
        if ($r.ExitCode -eq 0) { throw '--test without --what should fail' }
    }
    Test-Case 'set_profile.error_test_bad_what' {
        $r = Invoke-Cli set_profile '--test' $script:TestProfile '--what' 'nonsense'
        if ($r.ExitCode -eq 0) { throw "--what 'nonsense' should fail" }
    }
    Test-Case 'set_profile.error_mutex_create_and_edit' {
        $r = Invoke-Cli set_profile '--create' 'X3' '--edit' 'X4' '--platform' 'mssql' '--host' 'h' '--user' 'u' '--password' 'p' '--raw'
        if ($r.ExitCode -eq 0) { throw 'mutually-exclusive primary actions should be rejected' }
        if ($r.StdErr -notmatch 'mutually exclusive') { throw "stderr should mention mutual exclusion: $($r.StdErr)" }
    }

    # --- Test sub-commands run against TEST_LOCAL (created in Initialize-Suite) ---
    Test-Case 'set_profile.test_sql_source' {
        $r = Invoke-Cli set_profile '--test' $script:TestProfile '--what' 'sql-source'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch 'SQL Source exists') { throw "expected 'SQL Source exists' in output" }
    }
    Test-Case 'set_profile.test_connection' {
        $r = Invoke-Cli set_profile '--test' $script:TestProfile '--what' 'connection'
        Assert-ExitCode $r  # exits 0 even on failure (per source code contract) — just don't crash
    }
    Test-Case 'set_profile.test_options' {
        $r = Invoke-Cli set_profile '--test' $script:TestProfile '--what' 'options' '--resolve' '&users&'
        Assert-ExitCode $r
        # The test now mirrors Options.GenerateOptionFiles: company file (required)
        # plus table_locations (required, merged so table placeholders resolve).
        if ($r.StdOut -notmatch 'options\.101 found') {
            throw "expected options.101 (company file) to be reported as found"
        }
        if ($r.StdOut -notmatch 'table_locations found') {
            throw "expected table_locations to be reported as found (it is part of the resolution set)"
        }
    }
    Test-Case 'set_profile.test_options_bare_token' {
        # A bare token (no &...&) must be normalized to &token& and resolved via
        # table_locations. The scratch table_locations maps ba_basic_users; the
        # success line is "&ba_basic_users& = ...".
        $r = Invoke-Cli set_profile '--test' $script:TestProfile '--what' 'options' '--resolve' 'ba_basic_users'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch '&ba_basic_users& =') {
            throw "bare token 'ba_basic_users' should normalize to &ba_basic_users& and resolve via table_locations. stdout: $($r.StdOut)"
        }
    }
    Test-Case 'set_profile.test_table_locations' {
        $r = Invoke-Cli set_profile '--test' $script:TestProfile '--what' 'table-locations'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch 'Table locations file found') { throw "expected 'Table locations file found' in output" }
    }
    Test-Case 'set_profile.test_changelog' {
        # Source exits 0 whether gclog12 is on or off; we're proving the CLI is
        # drivable, not that the target DB has the SBN changelog plumbing.
        $r = Invoke-Cli set_profile '--test' $script:TestProfile '--what' 'changelog'
        Assert-ExitCode $r
        # Output should contain either a success or a recognized diagnostic.
        $combined = "$($r.StdOut)`n$($r.StdErr)"
        $expected = @('gclog12 option is off', 'gclog12 is on', 'ba_gen_chg_log_new', 'Could not resolve')
        if (-not ($expected | Where-Object { $combined -match [regex]::Escape($_) })) {
            throw "no recognized changelog diagnostic in output. stdout was: $($r.StdOut)"
        }
    }
    Test-Case 'set_profile.test_symlinks' {
        # Source catches UnauthorizedAccessException and reports counts; exits 0.
        # Shortcuts come from the SQL tree's create_links.sh when present, else the
        # built-in list; the scratch tree has neither create_links.sh nor the long
        # dirs, so it exercises the fallback + "already resolve / absent" summary.
        $r = Invoke-Cli set_profile '--test' $script:TestProfile '--what' 'symlinks'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch 'short-path resolution') {
            throw "expected 'short-path resolution' check output. stdout was: $($r.StdOut)"
        }
        if ($r.StdOut -notmatch 'create_links\.sh|built-in shortcut list') {
            throw "expected the shortcut source line (create_links.sh or built-in). stdout was: $($r.StdOut)"
        }
    }
    Test-Case 'set_profile.test_all' {
        # --what all is a distinct codepath that iterates over every test in order.
        $r = Invoke-Cli set_profile '--test' $script:TestProfile '--what' 'all'
        Assert-ExitCode $r
        # Each section is announced via WriteBright("-- $k --").
        foreach ($section in @('sql-source','connection','options','table-locations','changelog','symlinks')) {
            if ($r.StdOut -notmatch [regex]::Escape("-- $section --")) {
                throw "section '-- $section --' missing from --what all output"
            }
        }
    }
}

function Test-SelfMgmt {
    Write-Host "`n--- 6. Self-management ---" -ForegroundColor Cyan
    Test-Case 'version.print' {
        $r = Invoke-Cli runsql 'version'
        Assert-ExitCode $r
        # Version output is "<exename> <version>" e.g. "runsql 2.0.57"
        if ($r.StdOut -notmatch 'runsql \d+\.\d+\.\d+') {
            throw "expected 'runsql x.y.z' in output. stdout: $($r.StdOut)"
        }
    }
    # Verify every binary supports `version` (proves the shared subcommand wiring).
    Test-Case 'version.print_all_binaries' {
        foreach ($exe in @('set_profile','set_options','set_actions','set_required_fields',
                           'set_table_locations','set_messages','compile_msg','extract_msg',
                           'isqlline','runsql','runcreate','i_run_upgrade','iwho','iplan','iplanext')) {
            $r = Invoke-Cli $exe 'version'
            if ($r.ExitCode -ne 0) { throw "$exe version: exit $($r.ExitCode). stderr: $($r.StdErr)" }
            if ($r.StdOut -notmatch "$exe \d+\.\d+\.\d+") {
                throw "$exe version: expected '$exe x.y.z'. stdout: $($r.StdOut)"
            }
        }
    }
    Skip-Case 'version.update_dryrun' 'do not auto-trigger self-update from the regression suite'
    Test-Case 'configure.basic' {
        $r = Invoke-Cli runsql 'configure'
        Assert-ExitCode $r
        if ($r.StdOut -notmatch 'Compilers Configuration') { throw "expected 'Compilers Configuration' header" }
        if ($r.StdOut -notmatch 'Version:|Bin dir:|Platform:|Settings:|PATH:') {
            throw "expected configure status fields. stdout: $($r.StdOut)"
        }
    }
    Test-Case 'configure.all_binaries' {
        # Every binary supports `configure`. Smoke test that each exits 0.
        foreach ($exe in @('set_profile','set_options','isqlline','runsql','iwho')) {
            $r = Invoke-Cli $exe 'configure'
            if ($r.ExitCode -ne 0) { throw "$exe configure: exit $($r.ExitCode). stderr: $($r.StdErr)" }
        }
    }
}

function Test-ExclusionGuard {
    Write-Host "`n--- 7. Exclusion guard (transfer_data MUST stay out) ---" -ForegroundColor Cyan
    # Active check: scan this script for any non-comment, non-string mention
    # of transfer_data. If anyone ever wires it in, the suite fails loudly.
    Test-Case 'transfer_data.excluded' {
        $self = Get-Content $PSCommandPath -Raw
        # Strip comments and string literals to avoid false positives, then look
        # for an Invoke-Cli call against transfer_data.
        if ($self -match 'Invoke-Cli\s+transfer_data') {
            throw 'transfer_data is invoked somewhere in the suite - this is a policy violation'
        }
        # Also confirm the binary still exists (so we didn't accidentally delete it).
        $exe = Join-Path $script:Bin 'transfer_data.exe'
        if (-not (Test-Path $exe)) { throw "transfer_data.exe missing from bin dir - was it removed?" }
    }
}

# ---------- summary ----------

function Show-Summary {
    Write-Host "`n=== Summary ===" -ForegroundColor Cyan
    $byStatus = $script:Results | Group-Object Status
    foreach ($g in $byStatus) {
        $color = switch ($g.Name) { 'PASS' {'Green'} 'FAIL' {'Red'} 'GAP' {'Yellow'} default {'DarkGray'} }
        Write-Host ("{0,-5} {1}" -f $g.Name, $g.Count) -ForegroundColor $color
    }
    $failed = $script:Results | Where-Object Status -eq 'FAIL'
    if ($failed) {
        Write-Host "`nFailures:" -ForegroundColor Red
        foreach ($f in $failed) { Write-Host "  $($f.Id) - $($f.Reason)" -ForegroundColor Red }
    }
    return ($failed.Count)
}

# ---------- main ----------

try {
    Initialize-Suite

    Test-AdHocQueries
    Test-ScriptExecution
    Test-SetupCompile
    Test-Messages
    Test-ProfileManagement
    Test-SelfMgmt
    Test-ExclusionGuard

    $failCount = Show-Summary
    if ($failCount -gt 0) { exit 1 } else { exit 0 }
}
finally {
    Remove-Suite
}
