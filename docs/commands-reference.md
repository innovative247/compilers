# Commands Reference

Detailed documentation for all compiler commands.

---

## runcreate

Orchestrate multi-step database builds from a master script.

### Usage

```bash
runcreate create_file profile [output_file]
runcreate create_all GONZO
runcreate create_all GONZO build.log
runcreate create_all GONZO -O build.log
runcreate create_all G gonzo.log -bg    # background; use iwatch gonzo.log.out to follow
```

### Create File Format

```bash
# Comment lines start with #
#NT Windows-only line (stripped and executed on Windows)
#UNIX Unix-only line (skipped on Windows)

# Conditional execution based on options
&if_mssql& runsql mssql_specific.sql &dbpro&
&ifn_mssql& runsql sybase_specific.sql &dbpro&

# Path format: $ir>css>ss>ba>file -> {SQL_SOURCE}/css/ss/ba/file
runsql $ir>css>ss>ba>pro_users.sql &dbpro&

# Sequence flags
runsql script.sql &dbpro& -F1 -L5

# Supported commands
runsql, runcreate, i_run_upgrade, isqlline,
install_msg, install_required_fields, import_options,
create_tbl_locations, compile_actions
```

### Platform-Specific Lines

| Prefix | Behavior |
|--------|----------|
| `#NT` | Execute on Windows only |
| `#UNIX` | Execute on Unix/Linux/macOS only |
| `#` | Comment (always skipped) |

### Output File (-O)

When specified: file created at start, all output appends, console suppressed. Two files are created:
- `<file>.out` — all output
- `<file>.err` — only failed sections (quick failure summary)

---

## i_run_upgrade

Execute database upgrade scripts with version control.

### Usage

```bash
# 3 positional args: server, upgrade_no, script
i_run_upgrade <server/profile> <upgrade_no> <script_file> [-O output] [-e]

# 4 positional args: prepend database
i_run_upgrade <database> <server/profile> <upgrade_no> <script_file> [-O output] [-e]

# -D flag form (matches the binary's built-in Usage string)
i_run_upgrade <server/profile> <upgrade_no> <script_file> -D <database> [-O output] [-e]
```

`<upgrade_no>` is a **required positional** in all forms (the source does not
auto-extract it from the script filename when called as a standalone CLI;
filename-extraction only happens for the `runcreate`-embedded dispatch path).
Use the 4-arg / `-D` form when the upgrade must run against a specific
database; the 3-arg form falls through to the profile's default database.

Trailing positional is always the script file; the one before it is always
`<upgrade_no>`. See `Common.cs` → `i_run_upgrade_variables`.

### Upgrade Number Format

```
xx.yy.zzzzz  (cycle.release.ticket)
Example: 07.95.12345
```

### Process

1. Extract upgrade number from filename or argument
2. Check `ba_upgrades_check` (0=ready, 1=missing, 2=already run)
3. Execute SQL script via runsql
4. Update end time in upgrades table

---

## set_options (eopt, import_options)

Edit and compile options into the database.

### Usage

```bash
eopt PROFILE [-d] [-O output_file]
```

### Modes

- **Edit (default):** Edit profile + company options files
- **Add (-d):** Merge new options from `options.def` with `NEW->` prefix

### Source Files

```
{SQL_SOURCE}/CSS/Setup/
├── options.def                     # Master template
├── options.{COMPANY}               # Company-wide (e.g., options.101)
├── options.{COMPANY}.{PROFILE}     # Profile-specific
└── options.{PLATFORM}              # Platform-specific
```

### Compile Process

1. Parse company + profile options files
2. Convert to `:>` format
3. Insert into `w#options` work table
4. Execute `i_import_options` stored procedure
5. Delete options cache (force rebuild)
6. Compile table_locations

### Headless mode

When ANY headless action flag is present, the interactive menu is bypassed and
the requested action is performed unattended. Without flags the menu UX is
unchanged. Outcomes (file bytes, DB rows) match the manual flow exactly.

```bash
# Add a single option to options.def (mirrors menu → Add new option)
set_options PROFILE --add NAME --type value|onoff
                    --static | --dynamic
                    --default VALUE          # for --type value
                    --state on|off           # for --type onoff
                    [--description "TEXT"]
                    --mod-num MOD --mod-name USER --mod-reason "REASON"
                    [--merge-company] [--merge-profile]
                    [--customize NAME=VALUE]...
                    [--import]

# Merge new options from options.def into company / profile files
set_options PROFILE --merge-company [--customize NAME=VALUE]...
                    --mod-num MOD --mod-name USER --mod-reason "REASON" [--import]
set_options PROFILE --merge-profile [--customize NAME=VALUE]...
                    --mod-num MOD --mod-name USER --mod-reason "REASON" [--import]

# Sync (selective import without the checkbox UI)
set_options PROFILE --sync [--all-adds] [--add-only N1,N2]
                           [--all-removes] [--remove N1,N2]
                           [--customize NAME=VALUE]... [--import]

# Copy an options file
set_options PROFILE --copy options.SRC --to options.DST

# Standalone import (compile current files to DB)
set_options PROFILE --import
```

Examples:
```bash
# Add dynamic value option, merge to company, import
set_options PROD --add jake1 --type value --dynamic --default test1 \
                 --mod-num 07.95.27639 --mod-name CLAUDE --mod-reason "Add jake1" \
                 --merge-company --import

# Add static onoff option
set_options PROD --add quiet --type onoff --static --state off \
                 --mod-num 07.95.27640 --mod-name CLAUDE --mod-reason "Quiet flag"

# Sync everything missing in company file
set_options PROD --sync --all-adds --import
```

---

## set_table_locations (eloc, create_tbl_locations)

### Usage

```bash
eloc PROFILE
```

### Format

```
-> tablename &database_var& description
```

### Process

1. Parse `table_locations` source file
2. Resolve `&database_var&` placeholders
3. Truncate table_locations table
4. Insert all mappings

### Headless mode

Two Y/N prompts: edit `table_locations`, compile to DB.

```bash
set_table_locations PROFILE [--edit-header | --no-edit-header]
                            [--compile     | --no-compile]
                            [--skip-edit]
```

---

## set_messages (compile_msg, install_msg, extract_msg)

### Usage

```bash
compile_msg PROFILE    # Import messages to database
extract_msg PROFILE    # Export messages from database
```

### Message Types

| Type | Description |
|------|-------------|
| ibs | IBS framework messages |
| jam | JAM messages |
| sqr | SQR report messages |
| sql | SQL messages |
| gui | GUI/desktop app messages |

### GONZO as Canonical Source

GONZO (or alias G) is the authoritative message source. Export before importing to GONZO.

---

## set_actions (eact, compile_actions)

### Usage

```bash
eact PROFILE
```

Source files: `{SQL_SOURCE}/CSS/Setup/actions` and `actions_dtl`

### Headless mode

The interactive flow has three Y/N prompts: edit header file, edit detail file,
compile to DB. Each prompt has a flag override. Flags compose; absent flags
fall through to the prompt.

```bash
set_actions PROFILE [--edit-header | --no-edit-header]
                    [--edit-detail | --no-edit-detail]
                    [--compile     | --no-compile]
                    [--skip-edit]   # alias for --no-edit-header --no-edit-detail
```

Examples:
```bash
set_actions PROD --skip-edit             # compile only, no editor
set_actions PROD --skip-edit --no-compile # validate that files exist; do nothing
set_actions PROD --edit-header --no-edit-detail --compile
```

---

## set_required_fields (ereq, install_required_fields)

### Usage

```bash
ereq PROFILE
```

Source files: `{SQL_SOURCE}/CSS/Setup/required_fields` and `required_fields_dtl`

### Headless mode

Identical flag set to `set_actions` — same three Y/N prompts:

```bash
set_required_fields PROFILE [--edit-header | --no-edit-header]
                            [--edit-detail | --no-edit-detail]
                            [--compile     | --no-compile]
                            [--skip-edit]
```

---

## set_profile

Manage connection profiles in `settings.json`.

### Usage (interactive)

```bash
set_profile                   # main menu
set_profile EXISTING_NAME     # jump to that profile's submenu
set_profile NEW_NAME          # start the create wizard with name pre-filled
```

### Headless mode

Every menu action has a CLI equivalent. `--create` / `--edit` / `--view` /
`--copy` / `--delete` / `--test` are mutually exclusive primary actions.

```bash
# Create
set_profile --create NAME --platform mssql|sybase
                          --host H --user U --password PW
                          [--port N] [--company C] [--language L]
                          [--sql-source PATH] [--alias A,B,C]
                          [--raw]

# Edit (every field flag optional; only changed fields are written)
set_profile --edit NAME [field flags as in --create]
                        [--alias A,B] [--no-aliases]

# View, copy, delete
set_profile --view NAME
set_profile --copy SRC --to DST
set_profile --delete NAME --yes      # --yes is mandatory in headless mode

# Test (sub-menu equivalent)
set_profile --test NAME --what sql-source|connection|options|table-locations|changelog|symlinks|all
                        [--resolve PLACEHOLDER]   # used by --what options (default '&users&')
```

The connection test in headless mode does NOT prompt for credential retry on
failure — it prints the error and exits 0. Wrap the call and inspect output if
you need to react to a failure.

Examples:
```bash
set_profile --create PROD --platform mssql --host db.prod --user sa --password '...' \
                          --sql-source C:\src\sbn-services --company 101
set_profile --edit PROD --port 1444 --password 'new'
set_profile --test PROD --what all
set_profile --copy PROD --to STAGING
set_profile --delete OBSOLETE --yes
```

---

## transfer_data

Interactive menu-driven bulk data transfer between databases.

### Features

- Cross-platform (Windows, macOS, Linux)
- Uses managed BCP (`SqlBulkCopy` for MSSQL, INSERT for Sybase)
- State persistence for resume after interruption
- Row count verification
- Interactive checkbox UI for table/database selection

### Project Storage

Projects stored in `settings.json` under `"data_transfer"` key (separate from `"Profiles"`).

### Transfer Modes

- **Full**: Extract from source + insert to destination
- **Extract only**: Export to local BCP files
- **Insert only**: Import from existing BCP files

### Data Files

`./transfer_data_{project}/{database}_{table}.bcp` (tab-delimited)

---

## Changelog System

### ba_gen_chg_log Table

All `runsql` operations logged by default.

| Command | Logs? | Notes |
|---------|-------|-------|
| runsql | Yes | `--no-changelog` to disable |
| isqlline | No | By design (ad-hoc queries) |
| runcreate | Yes | Logs start/end with elapsed time |

### Requirements

1. `gclog12` option enabled (`act_flg = '+'`)
2. `ba_gen_chg_log_new` stored procedure exists

---

## Common Subcommands (all commands)

| Subcommand | Description |
|------------|-------------|
| `version` | Print version and exit |
| `update` / `install` | Download and install latest release |
| `configure` | Show configuration status, add to PATH |
