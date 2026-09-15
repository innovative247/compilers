# IBS Compilers

The IBS Compilers are a suite of cross-platform .NET 8 command-line tools for compiling SQL, managing database configurations, transferring data, and running upgrades against SBN's Sybase ASE and Microsoft SQL Server installations. They replace the legacy Python compiler suite and the original Unix shell wrappers around `isql` / `sqlcmd`.

## Where to get them

- **Repository:** [`innovative247/compilers`](https://github.com/innovative247/compilers) on GitHub. **Private** — you must have an Innovative247 GitHub account with explicit access granted before you can clone, view releases, or download installers. Contact Innovative to request access.
- **Releases:** [`https://github.com/innovative247/compilers/releases/latest`](https://github.com/innovative247/compilers/releases/latest)

Three platform builds are published with every release:

| Platform | Asset | Approx. size |
|---|---|---|
| Windows x64 | `compilers-net8-win-x64.zip` | 37 MB |
| Linux x64 | `compilers-net8-linux-x64.tar.gz` | 37 MB |
| macOS x64 | `compilers-net8-osx-x64.tar.gz` | 36 MB |

Each archive is **self-contained** — the .NET 8 runtime is bundled. Servers do not need a separate dotnet install.

## Installation

### Windows

One-liner (PowerShell, no admin required):

```powershell
irm https://raw.githubusercontent.com/innovative247/compilers/main/install.ps1 | iex
```

The installer:

- Detects and offers to remove any prior **Python** compiler install (the .NET version replaces it).
- Downloads the latest release ZIP into `%LOCALAPPDATA%\ibs-compilers`.
- Migrates an existing `settings.json` from a Python install if found, otherwise creates one from the bundled example.
- Runs `set_profile configure` to add the install dir to your user PATH.

After install, restart your terminal, then:

```powershell
set_profile             # configure database connections
runsql version          # confirm install
```

### Linux

```bash
mkdir -p ~/ibs-compilers && cd ~/ibs-compilers
curl -L https://github.com/innovative247/compilers/releases/latest/download/compilers-net8-linux-x64.tar.gz | tar xz
chmod +x runsql isqlline runcreate set_profile i_run_upgrade   # and the rest
./set_profile configure   # adds ~/ibs-compilers to PATH in ~/.bashrc
```

Restart your terminal, then run `runsql version` to confirm.

### macOS

```bash
mkdir -p ~/ibs-compilers && cd ~/ibs-compilers
curl -L https://github.com/innovative247/compilers/releases/latest/download/compilers-net8-osx-x64.tar.gz | tar xz
chmod +x runsql isqlline runcreate set_profile i_run_upgrade
./set_profile configure
```

macOS users may need to clear the quarantine attribute on first run:

```bash
xattr -d com.apple.quarantine *
```

## Updating

Every binary has a built-in self-update subcommand. The dispatcher accepts the verb in any of these equivalent forms — choose whatever you find natural:

```bash
runsql update           runsql -update          runsql --update
runsql install          runsql -install         runsql /update
isqlline update         set_profile -install    runcreate --update
```

All variants:

1. Query `https://api.github.com/repos/innovative247/compilers/releases/latest`.
2. Compare the release tag against the binary's compiled-in version.
3. If newer, download the platform-matching asset, extract over the install directory, and report the new version.
4. **Never** overwrites your `settings.json` (your credentials and profiles are preserved across updates).

A daily background check also runs the first time you invoke any compiler each day. If a newer version exists, you'll be prompted interactively:

```
A new version is available: v2.0.71 (current: v2.0.70)
Update now? [y/N]:
```

Decline and the update is silent until you choose to run `… update` yourself.

## How they run on each platform

All 22 commands run identically on Windows, Linux, and macOS. The installers are different (because PowerShell is the natural Windows entry), but every binary is the same .NET 8 self-contained executable on all three.

| Concern | Windows (NTFS) | Linux (ext4) | macOS (APFS) |
|---|---|---|---|
| Filesystem case | Insensitive | Sensitive | Sensitive (default) |
| Path separator | `\` (auto-normalized) | `/` | `/` |
| Settings location | `%LOCALAPPDATA%\ibs-compilers\settings.json` | `~/ibs-compilers/settings.json` | `~/ibs-compilers/settings.json` |
| Self-update mechanism | Renames running .exe to `.exe.old`, extracts new | tar replace + `chmod +x` | Same as Linux |
| State dir (update tracking) | `%LOCALAPPDATA%\ibs-compilers\` | `~/ibs-compilers/` | `~/ibs-compilers/` |

The compilers internally walk path components case-insensitively, so legacy `$ir/css/ss/...` references and modern `$ir/CSS/SS/...` references both resolve correctly even on case-sensitive filesystems.

## The 22 commands

### Connection / interactive

| Command | Purpose |
|---|---|
| `set_profile` | Create / edit / view / test database profiles in `settings.json` |
| `isqlline` | Run a single SQL statement against a profile (replaces native `isql -c`) |
| `runsql` | Run a SQL script file against a profile (replaces native `isql < script.sql`) |
| `iwho` | List active connections to a server (akin to `sp_who`) |
| `iwatch` | Tail the `.out` files written by background `runcreate` jobs |
| `iplan` / `iplanext` | Inspect query plans for a given SPID |

### Build / compile workflows

| Command | Purpose |
|---|---|
| `runcreate` | Run a multi-line `create_*` build file (dispatches to `runsql`, nested `runcreate`, `i_run_upgrade`, `compile_*`) |
| `i_run_upgrade` | Apply an upgrade script with `ba_upgrades_check` gating |
| `eact` / `set_actions` | Edit / compile `actions` files |
| `eloc` / `set_table_locations` | Edit / compile `table_locations` files |
| `eopt` / `set_options` | Edit / compile `options.def` and per-company / per-server option files |
| `compile_msg` / `set_messages` | Compile message files (BCP IN to `css.*_msg` tables) |
| `extract_msg` | Export message tables to source files (BCP OUT, round-trippable) |
| `compile_required_fields` / `set_required_fields` | Compile `required_fields` and `required_fields_dtl` files |
| `transfer_data` | Cross-server data transfer (interactive only — never agent-accessible) |
| `bcp_data` | Bulk copy data IN / OUT |

## Profiles

Profiles define database connection targets, kept in `settings.json` next to each platform's binaries. Each profile carries a host, port, credentials, platform (`SYBASE` or `MSSQL`), default company, default language, and the local path to the SQL source tree (`$ir`). Use `set_profile` to manage them.

```json
{
  "Profiles": {
    "MY_SERVER": {
      "PLATFORM": "SYBASE",
      "HOST": "10.20.30.40",
      "PORT": 5000,
      "USERNAME": "sa",
      "PASSWORD": "<from your password manager>",
      "COMPANY": "101",
      "DEFAULT_LANGUAGE": "1",
      "SQL_SOURCE": "C:\\path\\to\\current.sql",
      "RAW_MODE": false,
      "DATABASE": "",
      "ALIASES": ["MS"]
    }
  }
}
```

`SQL_SOURCE` should point at a working copy of the SBN_IR SVN tree. The compilers use it to find option files (`css/setup/options.*`), action files, table-location files, and `$ir`-relative paths inside `runcreate` scripts.

## Common workflows

### Compile one stored procedure

```bash
runsql my_proc.sql sbnpro MY_SERVER
```

### Run an ad-hoc query

```bash
isqlline "select count(*) from sysobjects where type='P'" sbnpro MY_SERVER
```

### Run a full create-file (background, with live tail)

```bash
runcreate create_all MY_SERVER my_run.log -bg
iwatch my_run.log.out
```

### Apply an upgrade script

```bash
i_run_upgrade MY_SERVER 07.95.NNNNN sct_07.95.NNNNN_bef.sql
```

`ba_upgrades_check` runs first; if the upgrade is already recorded, it exits without running.

## Real-time output streaming

Long-running procedures (e.g. `ma_algen`, cursor loops, anything emitting result-sets over time) stream rows back to the caller as the server delivers them, instead of buffering until the proc completes. This applies to `isqlline`, `runsql`, `runcreate` (via its nested `runsql` calls), and `i_run_upgrade`. Server-side TDS packet-fill thresholds may still buffer very small (<1 KB) result-sets — typical SBN workloads (queue rows, status records) exceed the threshold and stream as expected.

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `Profile 'XYZ' not found` | Run `set_profile --view XYZ` to confirm the profile exists. Use `set_profile` to create it. |
| `Company Option File Missing! .../css/setup/options.NNN` | The profile's `SQL_SOURCE` points at a tree without a populated `css/setup/` directory, or `COMPANY` doesn't match an existing options file. Check both. |
| Self-update succeeds but version unchanged | You may have multiple installs on PATH — run `which runsql` (Linux/macOS) or `where runsql` (Windows) and verify only one is active. |
| macOS: `cannot be opened because the developer cannot be verified` | Run `xattr -d com.apple.quarantine *` once in the install directory. |
| Linux: `Could not find platform-matching asset` | The release was published while the GitHub API was momentarily failing. Wait a few minutes and retry. |

## Source documentation

The authoritative developer reference inside Innovative is `.claude/tools/compilers/compilers.md` in the [`innovative247/dev`](https://github.com/innovative247/dev) handbook. This page is a customer / partner-facing distillation; the handbook covers internal release procedures, settings.json schema, headless `--add` / `--merge` flags for set_* commands, and the BCP source-format conventions.
