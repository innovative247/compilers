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
result set, where both fire). `must_contain` is a pipe-separated list of substrings that
must all appear, checked only where the case is expected to succeed.

| Case | What it covers |
|------|----------------|
| `overflow` | The originally reported statement. Mid-stream error on Sybase. |
| `divide-by-zero` | Mid-stream error whose severity differs per platform. |
| `bad-convert` | Conversion error raised while a result set is open. |
| `error-after-rows` | Error raised *after* rows have already streamed to the console. |
| `missing-table` | Error raised *before* any result set — the path that always worked. Guards the de-dupe. |
| `raiserror` | An explicit server-raised error. |
| `money-and-decimal` | `select 1 * 1.00 ,1* $1` — succeeds on Sybase/SQL Server and must render `1.00` / `1.0000` on both; on PostgreSQL `$1` is a bind placeholder, so it must be reported as an error rather than silently returning. |
| `decimal-scales` | `numeric(10,3)`, `money`, `int` and `float` in one row — each must keep its declared number of decimal places, and the non-decimal types must be left alone. All three platforms must render identical values. |

Every case runs on **all three platforms**, including PostgreSQL.

## Per-platform SQL variants

A few statements cannot express the same intent in every dialect. `convert(int,'abc')` is a
conversion error on Sybase and SQL Server but only a *syntax* error on PostgreSQL, which would
test nothing; `raiserror` and `&maxint& * 10` are likewise T-SQL-only.

Where that happens the case ships a variant next to its default file — `<name>.pg.sql`,
`<name>.ms.sql`, `<name>.syb.sql` — and the runner picks it up for the matching platform
automatically. The expectation still comes from `cases.tsv`, so the *behaviour* being asserted
stays identical even though the SQL differs:

| Case | PostgreSQL variant asserts |
|------|----------------------------|
| `overflow` | `2147483647 * 10` → `22003 integer out of range` |
| `bad-convert` | `cast('abc' as integer)` → `22P02 invalid input syntax` |
| `error-after-rows` | `cast(relname as integer) from pg_class` → `22P02` once rows are already streaming |
| `raiserror` | `do $$ ... raise exception ... $$` → `P0001` |
| `decimal-scales` | `numeric(10,3)` / `numeric(10,4)` / `int` / `float` render exactly as they do on Sybase and SQL Server |

`divide-by-zero`, `missing-table` and `money-and-decimal` need no variant — the default SQL is
valid on all three.

## Numeric scale

The last two cases guard a defect found alongside the error reporting one: the AseClient
returns `numeric` / `money` values as a `Decimal` whose scale has already been stripped,
so `1.00` arrived as `1` and printed as `"1"` where `isql` and SQL Server print `1.00`.
`SybaseExecutor` now re-applies the column's declared scale (`DeclaredScale`), taking it
from the schema table's `NumericScale` and from the type name for `money`/`smallmoney`,
which ASE reports as `NumericScale = -1` despite both being fixed at 4 decimal places.

`must_contain` is checked on every platform where the case is expected to succeed, so
these cases assert that Sybase and SQL Server render **identical** values — which is the
property that was broken.

## Relationship to the main suite

`tests/headless-suite.ps1` carries a four-case subset (`isqlline.error_midstream_sybase`,
`isqlline.error_midstream_no_dup`, `isqlline.sybase_decimal_scale`,
`isqlline.error_postgres_exit_code`) so a regression is caught by the normal suite run.
This corpus is the fuller, cross-platform version and the place to add new cases.
