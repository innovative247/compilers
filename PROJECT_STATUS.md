# IBS Compilers - Project Status

## Current Status

**Windows development complete. Next: Linux/Ubuntu testing.**

---

## Linux Testing Considerations

Areas requiring attention when testing on Linux:

| Area | Windows | Linux | Notes |
|------|---------|-------|-------|
| Symbolic links | Requires Admin | Native support | Should work better on Linux |
| Case sensitivity | Case-insensitive | Case-sensitive | May expose filename issues |
| Line endings | CRLF | LF | Options files, SQL scripts |

### Potential Issues

1. **Case sensitivity** - Linux filesystem is case-sensitive; Windows is not
2. **Line endings** - Options files and SQL scripts may have CRLF from Windows
3. **ODBC drivers** - pyodbc may need unixODBC and FreeTDS ODBC driver

---

## C# Reference Implementation

Location: `C:\_innovative\_source\sbn-services\Ibs.Compilers`

| File | Purpose |
|------|---------|
| `ibsCompilerCommon/options.cs` | Options class with v:/c: parsing |
| `ibsCompilerCommon/common.cs` | Core utilities, path mappings |
| `ibsCompilerCommon/runcreate.cs` | Build orchestration logic |

---

## Architecture Notes

### Core Principle

All reusable logic lives in `ibs_common.py`. Command scripts are thin wrappers that parse arguments and call shared functions.

### FreeTDS Encoding

- File reading: UTF-8 (cross-platform standard)
- tsql communication: CP1252 (FreeTDS limitation)
- Python transcodes automatically between the two

### Options System

| Prefix | Type | Resolution |
|--------|------|------------|
| `v:` | Static value | Compiled into SQL |
| `V:` | Dynamic value | Queried from database at runtime |
| `c:` | Static on/off | Compiled as comment blocks |
| `C:` | Dynamic on/off | Queried from database at runtime |

Static options (`v:`, `c:`) are resolved at compile time. Dynamic options (`V:`, `C:`) remain as runtime checks in the SQL.
