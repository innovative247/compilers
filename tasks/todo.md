# Task: SQL_SOURCE Path Validation in runsql

## Status: Complete

## Goal
Prevent compiling files that are outside the profile's SQL_SOURCE directory.

## Use Case
- Profile GONZO points to `C:\_innovative\_source\current.sql`
- User is editing a file in `C:\_innovative\_source\94.sql`
- runsql should error instead of compiling with mismatched options

## Implementation

**Location**: `src/commands/runsql.py` around line 245-250 (after `find_file()` returns)

**Logic**:
```python
sql_source = config.get('SQL_SOURCE', '')
if sql_source and profile_name:
    script_abs = os.path.normcase(os.path.abspath(script_path))
    source_abs = os.path.normcase(os.path.abspath(sql_source))

    if not script_abs.startswith(source_abs):
        print(f"ERROR: File is outside profile's SQL_SOURCE", file=sys.stderr)
        print(f"  File:       {script_path}", file=sys.stderr)
        print(f"  SQL_SOURCE: {sql_source}", file=sys.stderr)
        return 1
```

## Considerations
- Use `os.path.normcase()` for Windows case-insensitive path comparison
- Only check when using profiles (skip for direct `-H`/`-U` connections)
- Consider optional `--allow-external` flag to bypass when intentional
- Clear error message showing both paths

## Impact
- ~10 lines of code in runsql.py (lines 252-262)
- ~10 lines of code in runcreate.py main() (lines 741-751)
- No changes to ibs_common.py needed

## Validation Strategy
- **runsql**: Validates every direct command-line call
- **runcreate main()**: Validates the top-level create file only
- **runcreate nested**: No validation (calls run_create_file directly)
- **runsql from runcreate**: No validation (uses execute_runsql, not runsql command)

---

# Task: VSCode Integration for runsql

## Status: Pending

## Goal
Compile SQL files on hotkey (Ctrl+Shift+B) from VSCode.

## Implementation

Create `.vscode/tasks.json` in the SQL source directory:

```json
{
    "version": "2.0.0",
    "tasks": [
        {
            "label": "runsql",
            "type": "shell",
            "command": "runsql",
            "args": [
                "${file}",
                "sbnpro",
                "GONZO"
            ],
            "group": {
                "kind": "build",
                "isDefault": true
            },
            "presentation": {
                "reveal": "always",
                "panel": "shared"
            }
        }
    ]
}
```

## Notes
- `${file}` provides full absolute path of current file
- Change `sbnpro` to target database (sbnmaster, sbnwork, etc.)
- Change `GONZO` to target profile name
- The SQL_SOURCE validation task (above) makes this safer when working across multiple SQL directories

---

# Task: transfer_data - Cross-Platform Data Transfer Utility

## Status: Planning

## Goal
Create a new `transfer_data` command that transfers data between database servers (SYBASE or MSSQL) using INSERT statements to overcome the FreeTDS BCP 255-character field limit.

## Requirements

1. **New executable**: `transfer_data` (completely separate from set_profile)
2. **Cross-platform**: Support SYBASE ↔ MSSQL, SYBASE ↔ SYBASE, MSSQL ↔ MSSQL
3. **New settings section**: Store configurations in `"data_transfer"` section of settings.json (NOT in "Profiles")
4. **Isolation**: Existing code (set_profile, load_profile, etc.) must ONLY look at "Profiles" section
5. **INSERT-based**: Use SQL INSERT statements (not BCP) to avoid 255-char limit

### Database & Table Selection Requirements

6. **Database selection with wildcards**:
   - User specifies databases: `ibs, sbn*` (comma or space delimited)
   - Wildcards supported: `sbn*` matches sbnmaster, sbnpro, sbnwork, etc.
   - Query source server to expand wildcards to actual database list

7. **Table selection per database**:
   - For each database, query and list all tables
   - User can specify include patterns: `users, addr*`
   - User can specify exclude patterns: `w#*, srm_*` (work tables, temp tables)
   - Wildcards supported in both include and exclude

8. **Interactive table review**:
   - Show complete table list per database after pattern matching
   - All tables selected by default
   - Allow user to navigate list and toggle select/deselect individual tables
   - Display: `[X] users`, `[ ] w#temp`, etc.
   - Commands: `all` (select all), `none` (deselect all), `done` (confirm), numbers to toggle

9. **Transfer mode**:
   - User chooses: **APPEND** (insert only) or **TRUNCATE** (delete then insert)
   - Applies to entire transfer project (all tables)

### Safety & Control Requirements

10. **Pre-transfer confirmation**:
    - Always prompt before starting: "Ready to transfer X tables. Continue? [y/N]"
    - Show summary: source, destination, table count, mode (append/truncate)

11. **First-table review**:
    - Stop after first table completes successfully
    - Show result: rows transferred, time elapsed
    - Prompt: "First table complete. Continue with remaining X tables? [y/N]"
    - User must confirm before continuing

12. **Interruptible process**:
    - Allow user to stop at any time (Ctrl+C or 'q' during progress)
    - Save current state before exiting

### State Tracking & Resume Requirements

13. **Transfer state tracking**:
    - Track each table's status: `pending`, `in_progress`, `completed`, `failed`
    - Store in project config:
      ```json
      "TRANSFER_STATE": {
        "STARTED_AT": "2024-01-15T10:30:00",
        "LAST_UPDATE": "2024-01-15T10:35:00",
        "TABLES": {
          "sbnmaster..users": { "status": "completed", "rows": 5000, "elapsed": "12s" },
          "sbnmaster..branches": { "status": "in_progress", "rows": 2500 },
          "sbnmaster..addresses": { "status": "pending" }
        }
      }
      ```

14. **Resume capability**:
    - When user runs a project that was previously interrupted:
      - Detect incomplete transfer state
      - Prompt: "Previous transfer was interrupted. Resume from pending tables? [Y/n]"
      - If resume: skip completed tables, start with any `in_progress` or `pending`
      - If not resume: clear state and start fresh

15. **Progress display during transfer**:
    ```
    Transfer Progress:
    [1/10] sbnmaster..users      COMPLETED  (5000 rows, 12s)
    [2/10] sbnmaster..branches   IN PROGRESS  (2500/8000 rows...)
    [3/10] sbnmaster..addresses  PENDING
    ...
    Press 'q' to stop after current table, Ctrl+C to abort immediately
    ```

### Row Verification Requirements

16. **Row count verification**:
    - Before transfer: COUNT(*) on source table
    - After transfer: COUNT(*) on destination table
    - Compare counts and report result per table:
      ```
      [1/10] sbnmaster..users    VERIFIED   (5000/5000 rows)
      [2/10] sbnmaster..branches MISMATCH!  (3500 source, 3498 dest)
      ```

17. **Verification modes**:
    - **TRUNCATE mode**: dest count must equal source count
    - **APPEND mode**: dest count must increase by exactly source count
      - Record dest count BEFORE transfer starts
      - Verify: (dest_after - dest_before) == source_count

18. **Mismatch handling**:
    - On mismatch: pause and prompt user
      ```
      WARNING: Row count mismatch for sbnmaster..branches
        Source rows:      3500
        Transferred:      3500
        Destination rows: 3498 (2 missing)

      Options:
        [R]etry this table
        [S]kip and continue
        [A]bort transfer

      Choose [R/S/A]:
      ```
    - Log mismatch details in TRANSFER_STATE
    - Mark table status as `mismatch` if user skips

19. **Verification in state tracking**:
    ```json
    "TABLES": {
      "sbnmaster..users": {
        "status": "completed",
        "source_rows": 5000,
        "dest_rows": 5000,
        "verified": true,
        "elapsed": "12s"
      },
      "sbnmaster..branches": {
        "status": "mismatch",
        "source_rows": 3500,
        "dest_rows": 3498,
        "verified": false,
        "error": "2 rows missing"
      }
    }
    ```

20. **Final transfer report**:
    ```
    === Transfer Complete ===

    Total tables:    10
    Verified:        8
    Mismatches:      1  (sbnmaster..branches)
    Skipped:         1  (sbnmaster..temp_data)

    Total rows:      45,230
    Total time:      5m 32s

    Review mismatches? [y/N]:
    ```

### Parallel Transfer & Progress Requirements

21. **Multi-threaded transfers**:
    - Configurable thread count (default: 5)
    - Transfer up to N tables simultaneously
    - User prompt during project creation: "Parallel threads [5]:"
    - Store in OPTIONS: `"THREADS": 5`
    - Thread-safe state management (lock when updating TRANSFER_STATE)

22. **First-table review with threading**:
    - First table always runs single-threaded (for review)
    - After user confirms, enable parallel transfers for remaining tables
    - Example flow:
      ```
      [1/50] sbnmaster..users  TRANSFERRING... (single thread)

      --- First Table Complete ---
      Rows: 5000, Time: 12s, Verified: YES

      Continue with remaining 49 tables (5 parallel)? [y/N]: y

      [Now running 5 tables in parallel...]
      ```

23. **Real-time progress bars**:
    - Show progress bar per active transfer
    - Update in real-time as rows are inserted
    - Format:
      ```
      === Transfer Progress (5 threads) ===

      [2/50]  sbnmaster..branches   [████████████--------]  60%   2100/3500 rows
      [3/50]  sbnmaster..addresses  [██████--------------]  30%   1500/5000 rows
      [4/50]  sbnmaster..customers  [████████████████----]  80%   8000/10000 rows
      [5/50]  sbnmaster..invoices   [██------------------]  10%   500/5000 rows
      [6/50]  sbnmaster..payments   [--------------------]   0%   starting...

      Completed: 1/50 | Elapsed: 1m 23s | Press 'q' to stop
      ```

24. **Progress bar implementation**:
    - Use carriage return (`\r`) or ANSI escape codes for in-place updates
    - Refresh rate: ~2-4 times per second (avoid flicker)
    - Show: table name, bar, percentage, rows transferred/total
    - Handle terminal width gracefully (truncate long table names)

25. **Thread completion handling**:
    - When a thread finishes a table:
      - Update state (completed/mismatch)
      - If mismatch: pause ALL threads, prompt user
      - If verified: pick up next pending table
    - Display completed tables above active progress:
      ```
      DONE [1/50] sbnmaster..users      ✓ VERIFIED  5000 rows  12s

      [2/50]  sbnmaster..branches   [████████████--------]  60%
      ...
      ```

26. **Graceful shutdown with threads**:
    - 'q' key: finish current tables, don't start new ones
    - Ctrl+C: attempt graceful stop, save state for all in-progress tables
    - Wait for active threads to complete current batch before exit

## Design Decisions

### Why INSERT statements instead of BCP?
- FreeTDS `freebcp` has a 255-character field limit (documented in readme.md)
- This is already the established pattern for other compilers (set_options, set_table_locations, etc.)
- INSERT statements work reliably across both SYBASE and MSSQL

### Settings.json Structure
```json
{
  "Profiles": {
    "GONZO": { ... },
    "MSSQL_DEV01": { ... }
  },
  "data_transfer": {
    "MY_TRANSFER_PROJECT": {
      "SOURCE": {
        "PLATFORM": "SYBASE",
        "HOST": "54.235.236.130",
        "PORT": 5000,
        "USERNAME": "sa",
        "PASSWORD": "ibsibs"
      },
      "DESTINATION": {
        "PLATFORM": "MSSQL",
        "HOST": "127.0.0.1",
        "PORT": 1433,
        "USERNAME": "sa",
        "PASSWORD": "innsoft247"
      },
      "DATABASES": {
        "sbnmaster": {
          "DEST_DATABASE": "sbnmaster",
          "TABLES": ["users", "branches", "addresses"],
          "EXCLUDE_PATTERNS": ["w#*", "srm_*"]
        },
        "sbnpro": {
          "DEST_DATABASE": "sbnpro",
          "TABLES": ["*"],
          "EXCLUDE_PATTERNS": ["w#*"]
        }
      },
      "OPTIONS": {
        "MODE": "TRUNCATE",
        "BATCH_SIZE": 1000,
        "THREADS": 5
      },
      "TRANSFER_STATE": null
    }
  }
}
```

## Implementation Plan

### Phase 1: Core Infrastructure (ibs_common.py changes)
- [ ] 1.1 Add `load_data_transfer_projects()` function - reads only "data_transfer" section
- [ ] 1.2 Add `save_data_transfer_project()` function - saves to "data_transfer" section
- [ ] 1.3 Add `list_data_transfer_projects()` function
- [ ] 1.4 Verify `load_profile()` and related functions ONLY read "Profiles" section (confirmed)
- [ ] 1.5 Add `get_databases(config)` - query available databases from server
- [ ] 1.6 Add `get_tables(config, database)` - query tables in a database
- [ ] 1.7 Add `match_wildcard_pattern(name, patterns)` - support `*` wildcards

### Phase 2: Data Transfer Logic (ibs_common.py additions)
- [ ] 2.1 Add `get_table_columns(config, database, table)` - query column names/types
- [ ] 2.2 Add `get_table_row_count(config, database, table)` - for progress and verification
- [ ] 2.3 Add `extract_table_data(config, database, table, batch_size, offset)` - paginated SELECT
- [ ] 2.4 Add `generate_insert_statements(columns, rows)` - build INSERT SQL with escaping
- [ ] 2.5 Add `transfer_table(src, dest, db, table, options, progress_callback)` - single table transfer
  - Calls progress_callback(rows_done, total_rows) after each batch
- [ ] 2.6 Add `save_transfer_state(project, state, lock)` - thread-safe state persistence
- [ ] 2.7 Add `load_transfer_state(project)` - check for incomplete transfers
- [ ] 2.8 Add `verify_table_transfer(src, dest, db, table, mode, dest_before)` - row count verification
  - Returns: (verified: bool, source_count, dest_count, error_msg)

### Phase 2b: Threading & Progress (ibs_common.py additions)
- [ ] 2.9 Add `TransferWorker` class - thread worker for single table transfer
  - Handles: transfer, verification, progress reporting, error handling
- [ ] 2.10 Add `TransferThreadPool` class - manages worker threads
  - Thread count from OPTIONS (default 5)
  - Work queue of pending tables
  - Coordinates mismatch pausing across all threads
- [ ] 2.11 Add `ProgressDisplay` class - real-time progress bar rendering
  - ANSI escape codes for in-place updates
  - Multiple concurrent progress bars
  - Refresh rate limiting (250ms)
  - Terminal width detection

### Phase 3: CLI Command (transfer_data.py)
- [ ] 3.1 Create `src/commands/transfer_data.py` with main menu
- [ ] 3.2 **Create Project Wizard**:
  - Project name
  - Source connection (platform, host, port, user, password)
  - Destination connection (same fields)
  - Database selection with wildcards
  - Per-database table selection with include/exclude patterns
  - Interactive table review and toggle
  - Transfer mode (APPEND/TRUNCATE)
- [ ] 3.3 **Run Project**:
  - Check for incomplete state, prompt resume
  - Pre-transfer confirmation
  - First-table review pause
  - Progress display with interrupt handling
  - Row count verification after each table
  - Mismatch handling (Retry/Skip/Abort prompt)
  - State persistence after each table (with verification results)
  - Final summary report with verified/mismatch counts
- [ ] 3.4 Menu options: Create, Edit, Delete, Run, List, View State
- [ ] 3.5 Register command in `setup.py` entry points

### Phase 4: Testing & Polish
- [ ] 4.1 Test SYBASE → MSSQL transfer
- [ ] 4.2 Test MSSQL → SYBASE transfer
- [ ] 4.3 Test same-platform transfers
- [ ] 4.4 Test wildcard patterns (databases and tables)
- [ ] 4.5 Test interrupt and resume
- [ ] 4.6 Test large tables with batching
- [ ] 4.7 Test special characters in data (proper escaping)
- [ ] 4.8 Test row count verification (TRUNCATE mode)
- [ ] 4.9 Test row count verification (APPEND mode)
- [ ] 4.10 Test mismatch handling (Retry/Skip/Abort)
- [ ] 4.11 Test multi-threaded transfer (default 5 threads)
- [ ] 4.12 Test progress bar display with multiple concurrent transfers
- [ ] 4.13 Test graceful shutdown ('q' key and Ctrl+C)

## Key Technical Considerations

### Data Type Mapping
| SYBASE Type | MSSQL Equivalent | Notes |
|-------------|------------------|-------|
| varchar | varchar | Same |
| nvarchar | nvarchar | Same |
| int | int | Same |
| datetime | datetime | Same |
| text | varchar(max) | MSSQL prefers varchar(max) |
| image | varbinary(max) | MSSQL prefers varbinary(max) |

### Batching Strategy
- Default batch size: 1000 rows per INSERT transaction
- Commit after each batch to prevent transaction log overflow
- Show progress: "Transferred 5000/50000 rows..."

### String Escaping
- Single quotes: `'` → `''`
- NULL handling: Insert `NULL` keyword, not string 'NULL'
- Binary data: May need to skip or handle specially

### Error Handling
- Connection failures: Clear message, retry prompt
- Schema mismatch: Detect if dest table doesn't exist
- Data truncation: Warn if source data exceeds dest column width

## Files to Create/Modify

| File | Action | Description |
|------|--------|-------------|
| `src/commands/transfer_data.py` | CREATE | New CLI command |
| `src/commands/ibs_common.py` | MODIFY | Add data transfer functions |
| `setup.py` | MODIFY | Add entry point |
| `settings.json` | MODIFY | New "data_transfer" section (at runtime) |

## Estimated Impact
- ~600-700 lines in transfer_data.py (wizard + menu + state management + progress display)
- ~400-500 lines in ibs_common.py (core transfer + threading + progress classes)
- ~5 lines in setup.py (entry point)

## User Decisions (Resolved)
1. **Table selection**: YES - individual table selection with wildcards and exclusions
2. **Filtering (WHERE clause)**: NOT REQUIRED for initial version
3. **Schema mapping**: NOT REQUIRED for initial version (same table names on both sides)
4. **Audit logging**: State tracking provides this - completed tables show row counts

## Example User Flow

### Creating a Transfer Project
```
transfer_data

=== Data Transfer Utility ===

1. Create new project
2. Edit project
3. Delete project
4. Run project
5. List projects
6. Exit

Choose [1-6]: 1

--- Create New Project ---

Project name: GONZO_TO_MSSQL

--- Source Connection ---
Platform (SYBASE/MSSQL) [SYBASE]: SYBASE
Host: 54.235.236.130
Port [5000]: 5000
Username: sa
Password: ****

Testing connection... OK

--- Destination Connection ---
Platform (SYBASE/MSSQL) [MSSQL]: MSSQL
Host: 127.0.0.1
Port [1433]: 1433
Username: sa
Password: ****

Testing connection... OK

--- Database Selection ---
Enter databases to transfer (wildcards allowed, comma/space separated):
Example: ibs, sbn*

Databases: sbn*

Querying source server...
Matched databases: sbnmaster, sbnpro, sbnwork, sbnstatic

--- Table Selection for sbnmaster ---
Enter tables to include (* for all): *
Enter tables to exclude: w#*, srm_*, temp_*

Found 45 tables after exclusions.

Review tables for sbnmaster:
[X]  1. users
[X]  2. branches
[X]  3. addresses
[X]  4. customers
...
[X] 45. invoices

Commands: 1-45 toggle, 'all', 'none', 'done'
> done

Selected 45 tables from sbnmaster.

--- Table Selection for sbnpro ---
(... repeats for each database ...)

--- Transfer Options ---
Mode: [T]runcate or [A]ppend? T
Parallel threads [5]: 5

Project saved: GONZO_TO_MSSQL
Total: 3 databases, 127 tables
```

### Running a Transfer
```
transfer_data

Choose [1-6]: 4

Select project to run:
1. GONZO_TO_MSSQL

Choose: 1

=== Transfer Summary ===
Source:      SYBASE @ 54.235.236.130:5000
Destination: MSSQL @ 127.0.0.1:1433
Databases:   3
Tables:      127
Mode:        TRUNCATE

Ready to start transfer? [y/N]: y

[1/127] sbnmaster..users      TRANSFERRING...
        Rows: 5000/5000 (100%)

--- First Table Complete ---
Table:   sbnmaster..users
Rows:    5000
Time:    12 seconds
Mode:    TRUNCATE

Continue with remaining 126 tables? [y/N]: y

[2/127] sbnmaster..branches   TRANSFERRING... (1200/3500)
[Press 'q' to stop after current table]
```

### Resuming an Interrupted Transfer
```
transfer_data

Choose [1-6]: 4

Select project: GONZO_TO_MSSQL

*** Previous transfer was interrupted ***
Started:   2024-01-15 10:30:00
Last:      2024-01-15 10:42:15
Completed: 15/127 tables
Pending:   112 tables

Resume from where you left off? [Y/n]: y

Skipping 15 completed tables...
[16/127] sbnmaster..payments  TRANSFERRING...
```

---
