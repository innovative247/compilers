# Compilers — Test Suites

## Testing stages

| Stage | What it means | Command |
|-------|----------------|---------|
| 1. Iterating | Scoped/fast — one test or one area while making a change | `./tests/headless-suite.ps1 -OnlyId '<pattern>'` (e.g. `-OnlyId 'set_options.*'`) |
| 2. Feature done | Full local suite, once, before calling the work done | `./tests/headless-suite.ps1` |
| 3. CI | The repo's own `.github/workflows/` | none today — this repo has no `.github/workflows/` directory |

`headless-suite.ps1 -ListOnly` prints every test ID + status without running anything, useful
for finding the `-OnlyId` pattern you want for stage 1.

## Suites in this directory

- **`headless-suite.ps1`** — the main regression suite. One test per outcome row in
  `docs/feature-map.md`; see that file's header for the agent-driven contract it verifies.
- **`error-reporting/`** — cross-platform corpus (Sybase / SQL Server / PostgreSQL) proving
  server errors always reach the caller with a non-zero exit code. See its own
  `error-reporting/README.md` for `run.ps1` / `run.sh` usage.
- **`streaming/`** — harness proving `runsql` / `isqlline` / `runcreate` stream output
  incrementally instead of buffering, on Windows and WSL/Linux. See below.
- **`manual/`** — checklists for behavior the headless suite cannot exercise (raw-TTY UIs,
  process-inspection commands). Run these by hand; they are not part of any automated stage.

## `streaming/`

Moved from the team handbook (`.docs/unit-test/compilers/streaming/`) — testing material for
a single product belongs in that product's own repo, not the shared handbook.

- **Windows:** `./streaming/win_full_sweep.ps1` — sweeps every compiler CLI against a real
  profile (default `GONZO`), including the streaming cases (`runsql exec_streaming`,
  `runcreate create_test`).
- **WSL / Linux:**
  - `./streaming/wsl_full_sweep.sh` — sweep from a checkout binary.
  - `./streaming/wsl_isolated_sweep.sh` — sweep against an isolated installed build (set
    `BIN` at the top of the script to the install path).
  - `./streaming/wsl_case_setup.sh`, `./streaming/wsl_streaming_test_setup.sh`,
    `./streaming/wsl_post_rename_test.sh` — supporting setup/one-off scripts for specific
    streaming scenarios.
- `stream_probe.ps1` — spawns a compiler with `-O <tmpfile>`, tails it, and verdicts
  `STREAMING-PASS` / `STREAMING-FAIL` based on whether output arrived spread over time or
  all at once at the end. See the script header for `-Cmd` / `-CmdArgs` usage.
- `create_test`, `create_test_ir_local`, `create_test_wsl`, `exec_streaming_test.sql`,
  `pro_streaming_test*.sql`, `pro_streaming_forever.sql` — fixtures the sweeps above invoke.

These scripts assume a reachable database profile (e.g. `GONZO`) already configured via
`set_profile` — no credentials are hardcoded in them.

## `manual/process-commands.md`

Moved from the handbook alongside `streaming/`. Manual test cases for `iwho` / `iplan` /
`iplanext` against a live profile's process list.
