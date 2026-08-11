# Compilers — Feature Map (Headless Coverage)

Every menu path, prompt, and outcome across every compiler CLI command, with
the headless flag that drives it and the test ID in
`compilers/tests/headless-suite.ps1` that verifies it.

This is the contract for "an AI agent can drive every feature." If a row says
**COVERED**, an agent can reach that outcome without a TTY. If a row says
**GAP**, the corresponding test in the suite is marked `Skip` until the CLI
ships.

## Agent contract

When an AI agent drives these tools, **the agent itself is the editor**:

1. Gather every piece of data the command needs *upfront* — option
   name/type/value, action lines, table_locations entries, message body, MOD
   info. Ask the human for anything you don't already know **before** running
   the command.
2. Write the relevant source files directly using normal file-write tools
   (`options.def`, `options.<company>`, `actions`, `actions_dtl`,
   `table_locations`, `css.<type>_msg*`, `css.required_fields*`).
3. Invoke the compiler with structured action flags only — `--add`,
   `--merge-company`, `--import`, `--skip-edit --compile`, etc.

The `--edit-header` / `--edit-detail` flags exist for **interactive humans
only** — they launch `$EDITOR` and wait. An agent has no editor to launch.
These rows are marked `SKIP-AGENT` in the tables below: the binary still
supports them for human users, but they are not part of the agent contract
and the suite verifies them only as non-regression guards (via a fake editor
in `$env:EDITOR`).

## Scope

- **Test target:** `SRM_LOCAL` (127.0.0.1:1433 MSSQL). Mutating tests create
  a throwaway `TEST_LOCAL` profile that mirrors `SRM_LOCAL`'s connection;
  read-only tests run against the real profile. Postgres-platform tests are
  flag/arg-level only (no live Postgres server in the suite); a live Postgres
  target runs on port 5432.
- **Out of scope — never agent-accessible:** `transfer_data`. Excluded from the
  test suite entirely. If a future change adds CLI flags here, push back.
- **TTY-only — intentionally not headless:** `set_profile` main-menu options
  3 ("Add to IDE") and 4 ("Open settings.json"). Editor invocation requires a
  terminal.

## Status legend

| Status | Meaning |
|--------|---------|
| **COVERED** | Part of the agent contract. Headless flag in place, exercised by a test. |
| **GAP** | Should be part of the contract but isn't yet — `# TODO: needs CLI` skip placeholder. |
| **SKIP-AGENT** | Outside the agent contract (agent edits files directly). Binary still supports it for human users; suite verifies as a non-regression guard. |
| **SKIP** | Intentionally never CLI-drivable (e.g. menu items 3/4 in `set_profile`) or out of scope (`transfer_data`). |

---

## 1. Ad-hoc query / process inspection (already CLI-only)

These never had menus — listed for completeness so the test suite confirms
they still work end-to-end.

### `isqlline` — single SQL command

| Outcome | Flags | Test ID | Status |
|---|---|---|---|
| Execute one statement, print results | `isqlline "SQL" <db> <profile>` | `isqlline.basic` | COVERED |
| Echo input with line numbers | `-E` | `isqlline.echo` | COVERED |
| Capture to outfile | `-O <file>` | `isqlline.outfile` | COVERED |
| Force MSSQL/Sybase/Postgres mode | `-MSSQL` / `-SYBASE` / `-POSTGRES` | `isqlline.platform` / `isqlline.platform_postgres` | COVERED |
| Postgres refcursor auto-dereference — a `returns setof refcursor` result (cursor names) is FETCHed and streamed as N result sets in order, not printed as portal names (SR 52910) | (automatic, POSTGRES platform only; applies to `runsql` too) | manual — live PG target (no live Postgres in the suite; suite PG tests are flag-level) | COVERED |

### `iwho` — process list

| Outcome | Flags | Test ID | Status |
|---|---|---|---|
| Show all SPIDs | `iwho <profile>` | `iwho.all` | COVERED |
| Filter by SPID | `iwho <profile> <spid>` | `iwho.spid` | COVERED |
| Filter by login | `iwho <profile> <user>` / `<user%>` | `iwho.login` | COVERED |
| Polling timer | `-t <seconds>` | `iwho.timer` | COVERED |

### `iplan` / `iplanext` — execution plan

| Outcome | Flags | Test ID | Status |
|---|---|---|---|
| Plan for SPID | `iplan <profile> <spid>` | `iplan.basic` | COVERED |
| Plan + process info + SQL text dump | `iplanext <profile> <spid>` | `iplanext.basic` | COVERED |
| Override database | `-D <db>` | `iplan.database` | COVERED |
| Override host/port/user/pass | `-H -p -U -P` | `iplan.overrides` | COVERED |

---

## 2. Script execution

### `runsql` — execute SQL script

| Outcome | Flags | Test ID | Status |
|---|---|---|---|
| Run script in DB | `runsql <script> <db> <profile>` | `runsql.basic` | COVERED |
| Echo input | `-E` | `runsql.echo` | COVERED |
| Sequence loop | `-F <first> -L <last>` | `runsql.seq` | COVERED |
| Preview compiled SQL (no execute) | `--preview` | `runsql.preview` | COVERED |
| Skip changelog | `--changelog:n` | `runsql.no_changelog` | COVERED |
| Outfile + errfile split | `-O <file>` (auto-creates `.out` + `.err`) | `runsql.outfile` | COVERED |

### `runcreate` — multi-step build orchestrator

| Outcome | Flags | Test ID | Status |
|---|---|---|---|
| Run a create file | `runcreate <script> <profile>` | `runcreate.basic` | COVERED |
| Background + log file | `runcreate <script> <profile> <log> -bg` (NB: `-bg` is a wrapper concept, not in source) | `runcreate.bg` | COVERED |
| Outfile (named or `-O`) | `runcreate <script> <profile> <file>` / `-O <file>` | `runcreate.outfile` | COVERED |
| Comments + `#NT`/`#UNIX` platform lines | (file format, not a flag) | `runcreate.platform_lines` | COVERED |
| Dispatches `runsql`/`import_options`/`compile_actions`/`install_msg`/`install_required_fields`/`create_tbl_locations`/`i_run_upgrade` from inside the file | (file format) | `runcreate.dispatch_*` | COVERED |

### `i_run_upgrade` — versioned upgrade scripts

| Outcome | Flags | Test ID | Status |
|---|---|---|---|
| Run upgrade (3-arg) — `<server> <upgrade_no> <script>` | `i_run_upgrade <profile> <upgrade-no> <script>` | `iupgrade.smoke` | COVERED |
| Run upgrade (4-arg) — prepend explicit database | `i_run_upgrade <db> <profile> <upgrade-no> <script>` | `iupgrade.smoke` (verified by Common.cs `i_run_upgrade_variables`) | COVERED |
| Outfile / echo | `-O <file>` / `-e` | `iupgrade.outfile` | COVERED |
| Bails when already-run (`ba_upgrades_check` = 2) | (verified by re-running same upgrade) | `iupgrade.already_run` | COVERED |
| Bails when control table missing (`ba_upgrades_check` = 1) | (verified by point at a profile with no `ba_upgrades`) | `iupgrade.no_control` | COVERED |

---

## 3. Setup compile commands

All four use the shared `--edit-header` / `--no-edit-header` / `--edit-detail`
/ `--no-edit-detail` / `--compile` / `--no-compile` / `--skip-edit` flag
vocabulary defined in `InteractiveMenus.EditCompileBoolFlagNames`.

**Agent flow for all three:** write the source file(s) directly with the
correct content, then run `<cmd> PROFILE --skip-edit --compile`. The
`--edit-*` flags are for humans only and are excluded from the agent contract.

### `set_actions` (alias `eact`)

Interactive flow:
1. Y/N — edit `actions` (default Y)
2. Y/N — edit `actions_dtl` (default Y)
3. Y/N — compile to DB (default Y)

| Menu path | Outcome | Headless flag | Test ID | Status |
|---|---|---|---|---|
| Prompt 1 = Y | Launch editor on header file | `--edit-header` | `set_actions.edit_header` | SKIP-AGENT |
| Prompt 1 = N | Skip header editor (agent already wrote the file) | `--no-edit-header` or `--skip-edit` | `set_actions.skip_header` / `set_actions.no_edit_header` | COVERED |
| Prompt 2 = Y | Launch editor on detail file | `--edit-detail` | `set_actions.edit_detail` | SKIP-AGENT |
| Prompt 2 = N | Skip detail editor | `--no-edit-detail` or `--skip-edit` | `set_actions.no_edit_detail` | COVERED |
| Prompt 3 = Y | Run `compile_actions_main.Run` | `--compile` | `set_actions.compile_smoke` | COVERED |
| Prompt 3 = N | Print "Finished." and exit 0 | `--no-compile` | `set_actions.no_compile` | COVERED |
| (composite) | Skip both edits + compile only | `--skip-edit` | `set_actions.skip_edit` | COVERED |
| (error) | Source files missing → exit non-zero | (n/a) | `set_actions.error_header_missing` / `.error_detail_missing` / `.error_profile_missing` | COVERED |

### `set_required_fields` (alias `ereq`)

Identical menu/flag shape to `set_actions`. Targets `required_fields` and
`required_fields_dtl` source files; compile via
`compile_required_fields_main.Run`.

| Menu path | Outcome | Headless flag | Test ID | Status |
|---|---|---|---|---|
| Prompt 1 = Y | Launch editor on header | `--edit-header` | `set_required_fields.edit_header` | SKIP-AGENT |
| Prompt 1 = N | Skip header editor | `--no-edit-header` / `--skip-edit` | `set_required_fields.no_edit_header` / `.skip_edit` | COVERED |
| Prompt 2 = Y | Launch editor on detail | `--edit-detail` | `set_required_fields.edit_detail` | SKIP-AGENT |
| Prompt 2 = N | Skip detail editor | `--no-edit-detail` / `--skip-edit` | `set_required_fields.no_edit_detail` | COVERED |
| Prompt 3 = Y | Run `compile_required_fields_main.Run` | `--compile` | `set_required_fields.compile_smoke` | COVERED |
| Prompt 3 = N | exit 0 | `--no-compile` | `set_required_fields.no_compile` | COVERED |
| (error) | Source files missing → exit non-zero | (n/a) | `set_required_fields.error_*` | COVERED |

### `set_table_locations` (alias `eloc`)

Interactive flow:
1. Y/N — edit `table_locations` (default Y)
2. Y/N — compile to DB (default Y)

| Menu path | Outcome | Headless flag | Test ID | Status |
|---|---|---|---|---|
| Prompt 1 = Y | Launch editor | `--edit-header` | `set_table_locations.edit_header` | SKIP-AGENT |
| Prompt 1 = N | Skip editor | `--no-edit-header` / `--skip-edit` | `set_table_locations.no_edit_header` / `.skip_edit` | COVERED |
| Prompt 2 = Y | Run `compile_table_locations_main.Run` | `--compile` | `set_table_locations.compile_smoke` | COVERED |
| Prompt 2 = N | exit 0 | `--no-compile` | `set_table_locations.no_compile` | COVERED |
| (n/a — flag only) | Clear + re-merge the resolved options cache (`%TEMP%/options.*.tmp`), print its path, exit 0. With `--compile` it runs first so the compile can'''t read a stale cache. | `--rebuild` (alias `--rebuild-cache`) | `set_table_locations.rebuild` / `.rebuild_then_compile` / `.rebuild_location` / `.shows_cache_path` | COVERED |
| (error) | Source file missing → exit non-zero | (n/a) | `set_table_locations.error_source_missing` / `.error_profile_missing` | COVERED |

### `set_options` (alias `eopt`)

Interactive flow (dynamic menu, redrawn each loop):
- `1` Add new options → MOD info wizard → option-create loop → merge prompts
- `2` Edit existing options → file-edit prompts
- `3` Import (Sync Check) → checkbox UI for adds/removes
- `4..N` Edit `options.def` / `options.{company}` / `options.{company}.{db}*`
- `N+1..M` Copy each non-`def` file → new name prompt
- `99` Exit

| Menu path | Outcome | Headless flag(s) | Test ID | Status |
|---|---|---|---|---|
| `1` Add → wizard → review | Append new option to `options.def` with MOD markers + `CHG` header line | `--add NAME --type value\|onoff --static\|--dynamic --default V\|--state on\|off [--description T] --mod-num M --mod-name U --mod-reason R` | `set_options.add_value` / `set_options.add_onoff` | COVERED |
| `1` → Add → "Merge into company?" Y | Insert new options into `options.<company>` under MOD markers | `--merge-company` (with same MOD flags + optional `--customize K=V`) | `set_options.merge_company` | COVERED |
| `1` → Add → "Merge into profile?" Y | Same, into `options.<company>.<profile>` | `--merge-profile` | `set_options.merge_profile` | COVERED |
| `2` Edit | Launch editor on profile + company files | (agent writes the file directly — no flag) | — | SKIP-AGENT |
| `3` Import (Sync) — checkbox UI Add | Insert selected missing options | `--sync --all-adds` or `--sync --add-only N1,N2` | `set_options.sync_all_adds` / `set_options.sync_add_only` | COVERED |
| `3` Import (Sync) — checkbox UI Remove | Remove selected extras | `--sync --all-removes` or `--sync --remove N1,N2` | `set_options.sync_all_removes` / `set_options.sync_remove` | COVERED |
| Dynamic `4..N` Edit options.{X} | Launch editor on the picked file | (agent writes the file directly — no flag) | — | SKIP-AGENT |
| Dynamic `N+1..M` Copy options.{X} | Copy file to new name | `--copy options.SRC --to options.DST` | `set_options.copy` | COVERED |
| (n/a — flag only) | Clear + re-merge the resolved options cache, print its path, exit 0. Combined with another action (e.g. `--import`) it runs first. | `--rebuild` (alias `--rebuild-cache`) | `set_options.shows_cache_path` (+ shared with `set_table_locations.rebuild`) | COVERED |
| Post-action "Import to DB?" Y | Run `compile_options_main.Run` (also recompiles `table_locations`) | `--import` (composable with any other action) | `set_options.import_smoke` / `set_options.add_then_import_smoke` | COVERED |
| `--customize NAME=VALUE` | Override default during merge/sync (value or onoff) | `--customize K=V` (repeatable) | `set_options.customize_value` / `set_options.customize_onoff` | COVERED |
| (error) | Every validation surface (missing flags, bad combos, name length, duplicates, missing MOD info, bad copy target, mutex actions, malformed customize) | (n/a) | `set_options.error_*` (16 tests) | COVERED |
| `99` Exit | exit 0 | (default when no action flags) | — | COVERED |

**Validation surfaces (still enforced in headless):**
- `--add` requires `--type`, exactly one of `--static`/`--dynamic`, plus
  `--default` (value) or `--state` (onoff).
- Name ≤ 8 chars, must be unique against `options.def`.
- All merges + `--add` require all three of `--mod-num` / `--mod-name` /
  `--mod-reason`.
- `--customize NAME=VALUE` overrides per-option defaults during merge/sync.
  Onoff customizations must be `on`/`off` (or `+`/`-`).

---

## 4. Messages

### `extract_msg` — fully automated (export + recompile)

| Outcome | Flags | Test ID | Status |
|---|---|---|---|
| BCP OUT every message table → `<setup>/css.<ext>` flat files, then `compile_msg` to reimport | `extract_msg <profile>` | `extract_msg.basic` | COVERED |

No prompts — this is the "round-trip" agent-friendly entry point.

### `compile_msg` / `set_messages` — full headless parity

Both binaries route to `InteractiveMenus.RunSetMessages`. Headless mode kicks
in when any of `--import`, `--export`, `--on-saved`, or `--yes` is present;
otherwise the interactive menu is byte-for-byte unchanged.

Interactive flow:
- If profile is `GONZO` (or alias `G`): forced export-only, no menu.
- Otherwise:
  1. Mode prompt: `1` import, `2` export, `99` cancel
  2. If export: "WARNING: …overrides local message files. Are you sure?" Y/N
  3. If import: runs `compile_msg_main.Run`. If `<gui_messages_save>` has
     rows, 3-way prompt: `1` keep saved translations, `2` discard, `3` cancel.

| Menu path | Outcome | Headless flag | Test ID | Status |
|---|---|---|---|---|
| `1` Import → no saved rows | BCP IN every message file, run `i_compile_messages` / `i_compile_jam_messages` / `i_compile_jrw_messages` | `set_messages --import` | `set_messages.import` | COVERED |
| `1` Import → saved rows → keep | Skip save step, restore at end | `set_messages --import --on-saved keep` (default) | `set_messages.import_keep_saved` | COVERED |
| `1` Import → saved rows → discard | `DELETE <gui_messages_save>`, then save again | `set_messages --import --on-saved discard` | `set_messages.import_discard_saved` | COVERED |
| `1` Import → saved rows → cancel | exit 0, no changes | `set_messages --import --on-saved cancel` | `set_messages.import_cancel_saved` | COVERED |
| `2` Export → confirm | BCP OUT every message table → flat files | `set_messages --export --yes` (required for non-GONZO; replaces "Are you sure?" prompt) | `set_messages.export` | COVERED |
| `2` Export → no | exit 0 | (no flag — just omit `--export`) | — | COVERED |
| `99` Cancel | exit 0 | (no flag — no action) | — | COVERED |
| GONZO + `--export` | Forced export (no `--yes` required) | name-based auto-detect (`GONZO`/`G`) | `set_messages.gonzo_export` | SKIP (cannot run safely — would mutate canonical GONZO message files on disk; manual verification only) |
| GONZO + `--import` | refuse and exit 1 (production safety) | (no flag; replicates `CompileMsg.cs:32` guard at the headless dispatch step) | `set_messages.gonzo_import_blocked` | COVERED |
| (error) | Every validation surface — mutex, no primary action, missing `--yes`, bad `--on-saved` value, unknown profile | (n/a) | `set_messages.error_*` (5 tests) + `compile_msg.*` (2 tests) | COVERED |

**CLI surface:**

```
set_messages PROFILE
  ( --import | --export )           # mutually exclusive primary action
  [--on-saved keep|discard|cancel]  # only meaningful with --import; default keep
  [--yes]                           # required for --export on non-GONZO
                                    # (replaces the "Are you sure?" prompt)
```

Implementation notes (shipped):
- `RunSetMessagesHeadless` in `InteractiveMenus.cs` dispatches via
  `CliArgs.AnyPresent(args, "--import", "--export", "--on-saved", "--yes")`.
  The last two are included so a misuse like `--on-saved keep` without
  `--import` produces a clean error instead of a stdin hang.
- `OnSavedTranslations { Prompt, Keep, Discard, Cancel }` enum in
  `CompileMsg.cs` replaces the 3-way `Console.ReadLine` block. Default
  `Prompt` preserves interactive UX; `batch:true` callers (runcreate
  dispatch) and headless callers bypass the prompt.
- `compile_msg.exe` accepts the same flags (both binaries route to
  `RunSetMessages`).
- The flag list `SetMessagesBoolFlagNames = { "--import", "--export", "--yes" }`
  is handed to `CliArgs.StripLongFlags` from `Program.cs` in both
  `set_messages` and `compile_msg` entry points.
- The 3-way saved-translations prompt today fires inside `compile_msg_main.Run`.
  Plumb the choice as an enum on the call (default `Keep`); the existing
  `batch=true` path already implies `Keep`, so the new flag just makes it
  selectable.

---

## 5. Profile management

### `set_profile` — fully headless

Interactive main menu:
- `1` New profile (wizard, 6+ steps)
- `2` Existing profile → submenu: View / Test / Edit / Copy / Delete
- `3` Add to IDE → VSCode tasks generator
- `4` Open settings.json (launches editor)
- `99` Exit

| Menu path | Outcome | Headless flag | Test ID | Status |
|---|---|---|---|---|
| `1` New → wizard → save | Add profile to `settings.json` + create SQL-source symlinks | `--create NAME --platform mssql\|sybase\|postgres --host H --user U --password PW [--port N] [--company C] [--language L] [--sql-source PATH \| --raw] [--alias A,B] [--database DB]` | `set_profile.create_mssql` / `set_profile.create_raw` / `set_profile.create_postgres` | COVERED |
| `2` → Edit → save | Replace any of the fields above | `--edit NAME [any --create flag] [--no-aliases]` | `set_profile.edit_port` / `set_profile.edit_clear_aliases` | COVERED |
| `2` → View | Print profile details | `--view NAME` | `set_profile.view` | COVERED |
| `2` → Copy → save | Clone profile (aliases cleared) | `--copy SRC --to DST` | `set_profile.copy` | COVERED |
| `2` → Delete → confirm | Remove from `settings.json` | `--delete NAME --yes` (mandatory `--yes`) | `set_profile.delete_yes` / `set_profile.delete_without_yes_errors` | COVERED |
| `2` → Test → `1` SQL Source | Verify `IRPath` + `css/setup/` exists | `--test NAME --what sql-source` | `set_profile.test_sql_source` | COVERED |
| `2` → Test → `2` Connection | Attempt connection (3 query fallback for Sybase; Postgres probes `SELECT version()` falling back to `SELECT 1`) | `--test NAME --what connection` (no credential-retry prompt headless) | `set_profile.test_connection` | COVERED |
| `2` → Test → `3` Options | Resolve a placeholder against the full merged set — `options.<ServerType>` + `options.<company>` + `options.<company>.<server>` + `table_locations` (mirrors `Options.GenerateOptionFiles`). A bare token (e.g. `users`) is normalized to `&users&` so table names resolve; the file list shows each merge input incl. `table_locations`. The resolved cache file (`%TEMP%/options.[platform.]<company>.<profile>.tmp`, reused for 60 min) is reported with its path and age; `--rebuild` clears and re-merges it. | `--test NAME --what options [--resolve PLACEHOLDER] [--rebuild]` | `set_profile.test_options` / `.test_options_bare_token` / `.test_options_rebuild` | COVERED |
| `2` → Test → `4` Table locations | Verify `table_locations` file exists, then report the resolved options cache path/age and offer to clear + rebuild it | `--test NAME --what table-locations [--rebuild]` | `set_profile.test_table_locations` / `.test_table_locations_shows_cache_path` | COVERED |
| `2` → Test → `5` Changelog | Verify `gclog12` on, `ba_gen_chg_log_new` exists, insert test row | `--test NAME --what changelog` | `set_profile.test_changelog` | COVERED |
| `2` → Test → `6` Symbolic links | Create missing short-path shortcuts under `IRPath`, sourced from the tree's own `create_links.sh` (else built-in list); skips any whose short path/shortcut already exists | `--test NAME --what symlinks` | `set_profile.test_symlinks` | COVERED |
| `2` → Test → all | Run all six in order | `--test NAME --what all` | `set_profile.test_all` | COVERED |
| `3` Add to IDE | Write VSCode `tasks.json` | (TTY-only — explicit project decision) | — | SKIP |
| `4` Open settings.json | Launch editor on `settings.json` | (TTY-only — same reason) | — | SKIP |
| `99` Exit | exit 0 | (default with no flags + no positional) | — | COVERED |

**Validation surfaces (still enforced in headless):**
- Names: `[A-Z0-9_]+`, not reserved (`VERSION`/`UPDATE`/`INSTALL`/`CONFIGURE`/`V`).
- `--create` requires `--platform` (`mssql`/`sybase`/`postgres` only), `--host`,
  `--user`, `--password`, plus `--sql-source` unless `--raw`. `--database` is
  optional (Postgres schema-as-database target); omitted defaults to `postgres`.
- `--delete` without `--yes` errors out.
- Aliases must not collide with existing profile names or other aliases.

---

## 6. Bulk data copy

### `bcp_data` — bulk copy individual tables in or out

Never had a menu — the managed port of the Unix `export_sql_table_data` /
`import_sql_table_data` pair. One data file per table, named after the table
unless the datafile positional says otherwise. All BCP runs through
`ISqlExecutor.BulkCopy` (ADO.NET/Npgsql) — no native `bcp` process is ever
launched — so all three platforms are supported, POSTGRES included. The flag
set follows native Sybase/MSSQL `bcp` so the muscle memory carries over:
append by default, `-t` for the field terminator, datafile after the direction.

| Outcome | Flags | Test ID | Status |
|---|---|---|---|
| BCP OUT each table → `<table>.bcp`, print start/end + row count | `bcp_data <table...> OUT <profile>` | `bcp_data.out` | SKIP (needs an SBN table on the test target — Atlas/SRM_LOCAL has no CSS tables; manual verification only) |
| BCP IN each table: BCP IN (**append** — no truncate, as native bcp) as **one all-or-nothing unit** (or `-b` batches) → `update statistics` | `bcp_data <table...> IN <profile>` | `bcp_data.in` | SKIP (same; manual verification only) |
| Whole load is all-or-nothing by default — a row the server rejects leaves the table exactly as before the run (a transaction on MSSQL/POSTGRES, the bulk batch on Sybase) | (no flag) | — | SKIP (needs a live server + a row that fails; manual verification only) |
| Batch every N rows, traditional bcp style — a failure keeps the batches already landed | `-b <batchsize>` | `bcp_data.batchsize_flag_usage`, `bcp_data.error_bad_batchsize` | COVERED (offline: usage, `-b 0`/`-b x`/`-b -5` rejected, valid `-b` stripped as a flag) |
| Empty the table before loading (the legacy `import_sql_table_data` behavior, now opt-in) | `--truncate` (`--truncate:n` to negate) | `bcp_data.truncate_flag_usage` | COVERED (offline: usage + flag is accepted and not mistaken for a positional) |
| Field terminator other than tab — bcp's own flag, with its `\t` / `\n` / `\\` / `\0` escapes plus literal strings (`-t"\|"`, `-t,`) | `-t <field_terminator>` | `bcp_data.terminator_flag_usage`, `bcp_data.error_empty_terminator` | COVERED (offline: usage, accepted as attached or separate value, empty rejected) |
| Data file named explicitly, native-bcp style: `<table> in\|out <datafile> <profile>` (omitted → `<table>.bcp` in the current directory) | (positional) | `bcp_data.datafile_positional`, `bcp_data.error_datafile_multiple_tables` | COVERED (offline: the named file is the one reported missing on IN) |
| Data files are UTF-8 — written without BOM and LF-terminated on OUT, read BOM-tolerant on IN; on Sybase IN the server's charset is detected and logged (`server charset: cp850`) so the DATA_CHARSET reinterpretation is visible | (no flag) | — | SKIP (needs a live Sybase server; manual verification only) |
| Resolve a bare table name through `table_locations` — a name without `..` is looked up as `&name&` against the merged option set, exactly like `set_profile --test --what options`; a name that already contains `..` is used as-is | (no flag) | `bcp_data.error_unresolved_table` (resolution engaged + the unresolved diagnostic; the resolved `db..table` actually reaching the server rides on `bcp_data.out`/`.in`) | COVERED |
| Run against a POSTGRES profile | (platform comes from the profile) | `bcp_data.postgres_offline` | COVERED (arg/resolution level — no live Postgres in the suite) |
| Capture the run log to a file | `-O <file>` (log/output capture, as in `isqlline` — **not** the data file) | — | COVERED (shared `-O` plumbing, `isqlline.outfile`) |
| Credentials / platform override | `-U user` / `-P pass` / `-MSSQL` / `-SYBASE` / `-POSTGRES` | — | COVERED (shared `DefaultCommandVariables` plumbing) |
| Print usage | `bcp_data help` | `bcp_data.usage` | COVERED |
| Multiple tables: keep going after a per-table failure, exit 1 if any failed | (legacy loop behavior) | `bcp_data.error_missing_data_file` | COVERED |
| GONZO + `IN` → refuse and exit 1 before any connection (production safety) | (no flag; name-based auto-detect `GONZO`/`G`, same rule as `set_messages --import`) | `bcp_data.gonzo_in_blocked` | COVERED |
| (error) Every validation surface — missing server, bad direction, no tables, unknown profile, missing data file, unresolvable table | (n/a) | `bcp_data.error_*` (6 tests) | COVERED |

**CLI surface:**

```
bcp_data TABLE [TABLE...]           # bare name → resolved via table_locations
                                    # db..table → used as-is
         ( IN | OUT )               # direction (case-insensitive)
         [DATAFILE]                 # native-bcp slot; single table only
                                    # omitted → <table>.bcp in the cwd
         PROFILE                    # server/profile — always last
         [-t field_terminator]      # default tab; \t \n \\ \0 escapes honored
         [-b batchsize]             # commit every N rows (IN)
                                    # omitted → the whole load is one unit
         [--truncate]               # empty the table first (IN); off by default
         [-U user] [-P pass]        # credential override
         [-O outfile]               # run log — not the data file
         [-MSSQL | -SYBASE | -POSTGRES]
```

The datafile slot is resolved positionally: `IN`/`OUT` second-to-last means no
data file, third-to-last means the token between it and the profile is one.

**Error surfaces (all exit 1, message on stderr):**
- Fewer than two positionals / no server — plus usage.
- Direction is not `IN` or `OUT` — plus usage.
- No table names in front of the direction — plus usage.
- `-t` with no value — plus usage.
- `-b` with anything but a positive row count (`0`, `-5`, `x`) — plus usage.
- A data file with more than one table (`a data file requires exactly one
  table`) — plus usage.
- Unknown profile (`ProfileManager.ValidateProfile`).
- `IN` against `GONZO`/`G` — refused before the executor is created.

**Per-table failures (message on the run log, loop continues, exit 1 at the end):**
- `IN` with no data file — `<table>.bcp` in the current directory, or the one
  named positionally (checked before anything connects).
- A bare name with no `table_locations` entry — reported as unresolved rather
  than sent to the server as a literal `&name&`.
- `truncate` (only with `--truncate`) / `BulkCopy` failure.

**Atomicity notes:**
- **A load is all-or-nothing; an export is not.** No `-b`: the whole file lands
  or none of it does, so a table is never left half-loaded. With `-b N`: the
  boundary moves to every N rows, exactly like traditional bcp — a failure
  discards only the batch it happened in and everything before it stays. OUT
  opens nothing: a single `SELECT` / `COPY TO STDOUT` is already
  statement-consistent, and native `bcp out` has no transaction either.
- **MSSQL** — an external `SqlTransaction` passed to `SqlBulkCopy`, whose
  `BatchSize` then only controls how often rows go over the wire;
  `SqlBulkCopyOptions.UseInternalTransaction` with `BatchSize = N` for `-b`.
- **POSTGRES** — an `NpgsqlTransaction` around the `COPY` (COPY joins the
  connection's open transaction); `-b` is one transaction + one COPY per chunk.
- **Sybase — the bulk batch, not a transaction.** ASE refuses a bulk insert
  inside one: `BULK INSERT command not allowed within multi-statement
  transaction.` The `AseBulkCopy(AseConnection, AseTransaction)` overload exists
  but the server rejects it and the driver reports `Connection entered broken
  state`, so **no transaction is ever opened for a Sybase load**. ASE's own unit
  of atomicity does the job: a row the server rejects discards the entire
  in-flight batch. Measured on ASE 16 with a 1500-row load whose 1200th row
  violates a unique index — one batch: 0 rows landed; 500-row batches: the
  first 1000 rows stayed and the failing batch was discarded whole. So `-b N`
  is N-row bulk batches, and the guarantee matches the other two platforms by a
  different mechanism. Do not "fix" this by adding a transaction.
- The status line each import prints says which one is in play — `Loading in a
  single transaction …` on MSSQL/POSTGRES, `Loading as one bulk batch — the
  server discards it on failure.` on Sybase.
- `--truncate` runs as its own statement **outside** the load transaction, and
  `update statistics` / `analyze` runs after it has committed. Neither is
  transaction-safe on Sybase ASE: ASE refuses `truncate table` inside a user
  transaction unless the database has `ddl in tran` set. Do not reason about
  this from MSSQL behavior — the two servers differ here.

**Platform notes:**
- `truncate` and the post-import stats call go out with the **bare** table name
  plus the database handed to the executor — initial catalog on MSSQL/Sybase,
  `search_path` on POSTGRES (where the `db..table` db part is a schema). There
  is no portable `db..table` form in plain SQL; `BulkCopy` still gets the
  qualified name and each executor splits it itself.
- The post-import stats call is `update statistics <table>` on Sybase/MSSQL and
  `analyze <table>` on POSTGRES. Either way the rows are already in, so a
  refusal is logged as a warning and the table still counts as imported.
- On IN the executors print their own `N rows copied.`; `bcp_data` adds the
  count line only for OUT (where `BulkCopyOut` prints just
  "rows successfully extracted").
- Data files are UTF-8 on every platform (`ibs_compiler_common.OpenBulkWriter` /
  `ReadBulkLines` — no BOM out, BOM tolerated in). MSSQL is collation-aware and
  POSTGRES is UTF-8, so that is the whole story there; on Sybase the char data
  still passes through `SybaseExecutor`'s DATA_CHARSET reinterpretation against
  the server's own charset, which IN logs as `server charset: <name>`. No
  charset is ever pinned in a connection string — the server declares it.
- The `-t` terminator is bcp-faithful: the delimiter is never quoted or escaped
  inside data. On POSTGRES the COPY wire format still separates fields with
  tabs, so its tab escaping is unchanged for the default and a tab inside data
  simply stays a tab when the terminator is something else.

---

## 7. Self-management (all CLI-only — present on every binary)

| Outcome | Flags | Test ID | Status |
|---|---|---|---|
| Print version | `<cmd> version` | `version.print` | COVERED |
| Self-update from GitHub releases | `<cmd> update` / `<cmd> install` | `version.update_dryrun` | COVERED (smoke only — don't actually upgrade) |
| Show config, fix PATH/settings.json | `<cmd> configure` | `configure.basic` | COVERED |

---

## 8. Out-of-scope (explicitly never exposed to agents)

### `transfer_data`

Bulk data transfer between databases. Interactive checkbox UI, state files,
BCP files on disk. **Permanently excluded from headless mode and from this
test suite by team policy.** Any future request to add `--flags` here must be
rejected — see project notes / session record.

### `set_profile --add-to-ide` and `--open-settings`

Both require launching an editor / pointing to a user's IDE config. There is
no useful headless equivalent — an agent that needs VSCode tasks should write
`tasks.json` directly.

---

## Gap summary

**No outstanding gaps.** Every menu path on every in-scope command is either
COVERED by the test suite or SKIP-by-design with a documented rationale.
Suite tally after PG-support wave 4: **139 PASS / 5 SKIP / 0 FAIL / 0 GAP**.

The skips:
- `iwho.timer` — polling blocks indefinitely.
- `version.update_dryrun` — would trigger real GitHub self-update.
- `runcreate.bg` — `-bg` is a wrapper concept, not a source-level flag.
- `set_messages.gonzo_export` — would mutate canonical GONZO message files
  on disk (CLI behavior is proven equivalent to non-GONZO export via the
  `gonzo_import_blocked` test; export round-trip itself is manual only).
- `runcreate.platform_lines_postgres` — `#NT`/`#UNIX` create-file prefixes are OS
  dispatch lines, not DB-platform lines; there is no Postgres-specific create-file
  directive to mirror.
- `bcp_data.out` / `bcp_data.in` — need a real SBN table on the test target
  (Atlas/SRM_LOCAL has no CSS tables), and `in` would truncate it. Every
  `bcp_data` validation surface, the GONZO guard, the POSTGRES platform path,
  and table resolution are covered offline; the BCP round-trip itself is
  manual only.

The five SKIP-AGENT rows (`--edit-*` flags across the three setup-compile
commands plus set_options menu items 2 and 4..N) are not gaps either — they
are documented out-of-scope for the agent contract (agent writes files
directly).
