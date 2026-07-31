# Error-reporting corpus

A server error must always reach the person running the command: printed with its
message number, and reflected in a non-zero exit code. This corpus is the
reproducible proof of that, on **Windows, Linux and macOS**, against **Sybase ASE,
SQL Server and PostgreSQL**.

## Why it exists

`isqlline 'select &maxint& * 10' sbnpro G` used to print an empty result grid,
`(0 rows affected)`, and exit **0**. The server had reported `Msg 3606 Arithmetic
overflow occurred` and the compilers swallowed it.

Two independent defects caused that:

1. **Sybase, streaming mode.** With `CommandBehavior.SequentialAccess` the AseClient
   hands the reader back as soon as the first result set arrives, then keeps parsing
   tokens on a background pump thread. Any error the server raises *after* that point
   is delivered through the connection's `InfoMessage` event — the `AseException` the
   driver also builds is thrown on the pump thread, after `readerSource` already has a
   result, so it goes nowhere. `SybaseExecutor.OnInfoMessage` was discarding everything
   with `Severity >= 11` on the assumption that the exception would carry it.
   Errors raised *before* the first result set (a missing table, a syntax error) were
   never affected — which is exactly why the problem was easy to miss.
2. **PostgreSQL, exit code.** `ExecReturn` is a **struct**. `PostgresExecutor.RunChunk`
   took it by value, so the `Returncode = false` it set on an error was written to a
   copy and thrown away. Every PG error printed its message and still exited 0.

SQL Server was never affected: `Microsoft.Data.SqlClient` raises class >= 11 as a
`SqlException` on `Read()`, so those errors always reached the caller's catch.

## Running it

Both runners read the same `cases.tsv` and assert the same things. Use whichever
matches the box you are on:

```powershell
# Windows / Linux / macOS, PowerShell 7+
./run.ps1 -Sybase GONZO:sbnpro -Mssql SRM_LOCAL:master -Postgres PGTEST:pgtest
./run.ps1 -Sybase GONZO:sbnpro -Bin ../../bin/win-x64      # test an un-installed build
```

```bash
# Linux / macOS (also works in Git Bash)
./run.sh --sybase GONZO:sbnpro --mssql SRM_LOCAL:master --postgres PGTEST:pgtest
./run.sh --sybase GONZO:sbnpro --bin ../../bin/linux-x64
```

Each platform is `PROFILE:DATABASE`. Omit a platform to skip it; omit `-Bin`/`--bin`
to test whatever is on `PATH`. Exit code is 0 only when every case passed.

Every case runs **twice** per platform — once as inline SQL through `isqlline`, once
as a script file through `runsql` — because the two take different paths into the
executor and the original bug was equally invisible in both.

## What each case asserts

`cases.tsv` gives the expected outcome per platform. `error:<substring>` means the run
must exit non-zero **and** print that substring **exactly once** — a second copy means
the error is being reported twice (the driver's info-message channel *and* the
exception it also raises, which is a real hazard for errors raised before the first
result set, where both fire).

| Case | What it covers |
|------|----------------|
| `overflow` | The originally reported statement. Mid-stream error on Sybase. |
| `divide-by-zero` | Mid-stream error whose severity differs per platform. |
| `bad-convert` | Conversion error raised while a result set is open. |
| `error-after-rows` | Error raised *after* rows have already streamed to the console. |
| `missing-table` | Error raised *before* any result set — the path that always worked. Guards the de-dupe. |
| `raiserror` | An explicit server-raised error. |
| `money-and-decimal` | `select 1 * 1.00 ,1* $1` — succeeds on Sybase/SQL Server, and on PostgreSQL `$1` is a bind placeholder, so it must be reported as an error rather than silently returning. |

## Known divergence (not an error-reporting defect)

`money-and-decimal` renders differently per platform:

| Platform | Output |
|----------|--------|
| SQL Server | `1.00` and `1.0000` |
| Sybase ASE | `1` and `1` |

The AseClient returns those `numeric(14,2)` / `money` values as a `Decimal` with the
scale already stripped, so the trailing zeros are gone before the renderer sees them.
`isql` prints `1.00`. The corpus deliberately does **not** assert the rendered value —
it is a numeric-formatting fidelity issue, separate from error reporting, and fixing it
means either formatting to the schema's `NumericScale` in `SybaseExecutor` (the `money`
type reports `NumericScale = -1`, so it needs a type-name special case) or fixing the
driver. Left open on purpose.

## Relationship to the main suite

`tests/headless-suite.ps1` carries a three-case subset (`isqlline.error_midstream_sybase`,
`isqlline.error_midstream_no_dup`, `isqlline.error_postgres_exit_code`) so a regression
is caught by the normal suite run. This corpus is the fuller, cross-platform version and
the place to add new cases.
