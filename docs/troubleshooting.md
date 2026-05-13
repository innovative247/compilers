# Compilers Troubleshooting

## Connection Issues

### Command not found

**Symptom:**
```
bash: runsql: command not found
```

**Solution:**
```bash
set_profile configure
```
This adds the install directory to your PATH.

### Connection failed / timeout

**Symptom:**
```
ERROR: Connection to database failed
```

**Check:**
1. Profile settings: `set_profile` → View profiles
2. Network connectivity to host:port
3. VPN connected (if required)
4. Database server running

**Test:**
```bash
isqlline "SELECT 1" master PROFILE_NAME
```

### Authentication failed

**Check:**
1. Username/password in settings.json
2. Account not locked
3. Correct platform (SYBASE vs MSSQL) in profile

---

## Options System Issues

### Required file not found

**Symptom:**
```
ERROR: Required file 'options.def' not found.
Search context:
  Location: C:\_source\current.sql\CSS\Setup
  Company:  101
  Profile:  GONZO
```

**Solution:**
1. Verify `SQL_SOURCE` in profile points to correct directory
2. Check `CSS/Setup/` subdirectory exists
3. Required files: `options.def`, `options.{company}`, `table_locations`

### Placeholder not resolved

**Symptom:** Output contains literal `&placeholder&`

**Solutions:**
1. Check spelling in option files (case-sensitive)
2. Delete stale cache: `rm {SQL_SOURCE}/CSS/Setup/temp/{profile}.options.tmp`
3. Add missing placeholder to appropriate options file

### Conditional block not working

**Check:**
1. Is `c:feature` defined in option files?
2. Is it `+` (enabled) or `-` (disabled)?
3. Spelling must match exactly

---

## File Path Issues

### Outside profile's SQL_SOURCE

**Symptom:**
```
ERROR: You are outside of profile GONZO's path
```

**Solutions:**
1. Use correct profile for the SQL source tree
2. Create new profile with correct `SQL_SOURCE`

### Symbolic links not created

**Windows:** Requires Administrator privileges (falls back to path expansion)

---

## Build Issues (runcreate)

### Script not found

**Check:**
1. Symbolic links exist (`CSS/ss/ba` → `CSS/SQL_Sources/Basics`)
2. File exists at expected path
3. Case sensitivity (Linux is case-sensitive)

### Conditional block not executing

Check conditional flags in options cache:
```bash
grep "if_mssql" "{SQL_SOURCE}/CSS/Setup/temp/{profile}.options.tmp"
```

---

## Changelog Issues

### Changelog not logging

Operations not appearing in `ba_gen_chg_log`.

**Check:**
1. `gclog12` option enabled: `select * from options where id = 'gclog12'` (act_flg = '+')
2. `ba_gen_chg_log_new` procedure exists
3. Not running in RAW_MODE
4. Not using `--no-changelog` flag

---

## Installation Issues

### Version check fails

If daily version check errors, it silently continues (non-blocking). To manually check:
```bash
runsql version
runsql update
```

### Settings.json not found

```bash
set_profile configure
```
This will copy `settings.json.example` if no settings file exists.

---

## Common Error Messages

| Error | Cause | Solution |
|-------|-------|----------|
| `Profile 'X' not found` | Typo or missing profile | Run `set_profile` |
| `Could not resolve &X&` | Undefined placeholder | Add to options file |
| `Connection failed` | Network/auth issue | Check host, port, credentials |
| `Outside profile's path` | Wrong profile | Use correct profile |
| `Required file not found` | Missing options/table_locations | Check SQL_SOURCE path |

---

## Diagnostic Commands

```bash
runsql version                              # Check version
set_profile configure                       # Check environment
isqlline "SELECT 1" master PROFILE          # Test connection
runsql --preview script.sql db PROFILE      # Preview without executing
runsql --debug script.sql db PROFILE        # Verbose output
```
