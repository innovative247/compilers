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
