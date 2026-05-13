# Options System (Soft-Compiler)

The Options system provides compile-time placeholder resolution for SQL files, enabling a single codebase to work across different database servers, platforms, and environments.

## Overview

SQL files use `&placeholder&` syntax that gets replaced at compile time:

```sql
-- Before compilation:
select * from &users& where company = &cmpy&

-- After compilation (GONZO profile):
select * from sbnmaster..users where company = 101
```

## Option Types

### Static Values (v:)

Replaced at compile time with literal values.

**Definition:**
```
v:dbtbl <<sbnmaster>> Main tables database
v:af2 <<2>> Money decimal places
```

**Usage:**
```sql
select * from &dbtbl&..users     -- becomes: sbnmaster..users
select round(amount, &af2&)      -- becomes: round(amount, 2)
```

### Dynamic Values (V:)

Stored in database, queried at runtime. NOT compiled into SQL.

**Definition:**
```
V:timeout <<30>> Connection timeout (user can change)
```

### Static Conditionals (c:)

Enable/disable code blocks at compile time using comment wrappers.

**Definition:**
```
c:mssql - SQL Server platform (disabled)
c:sybase + Sybase platform (enabled)
```

**Generated Placeholders:**
- `&if_mssql&` / `&endif_mssql&` - Wraps MSSQL-specific code
- `&ifn_mssql&` / `&endifn_mssql&` - Wraps non-MSSQL code

**Usage:**
```sql
&if_mssql&
-- MSSQL-specific code
select top 10 * from users
&endif_mssql&

&ifn_mssql&
-- Sybase-specific code
set rowcount 10
select * from users
set rowcount 0
&endifn_mssql&
```

**Compiled Output (when mssql is disabled):**
```sql
/*
-- MSSQL-specific code
select top 10 * from users
*/


-- Sybase-specific code
set rowcount 10
select * from users
set rowcount 0

```

### Dynamic Conditionals (C:)

Checked at runtime from `options.act_flg`. NOT compiled.

**Definition:**
```
C:feature_x + New feature toggle
```

### Table Locations (->)

Map logical table names to physical database locations.

**Definition:**
```
-> users &dbtbl& User master table
-> invoices &dbtbl& Invoice headers
-> audit_log &dbwrk& Audit trail (work database)
```

**Generated Placeholders:**
- `&users&` → `sbnmaster..users`
- `&db-users&` → `sbnmaster`

**Usage:**
```sql
select * from &users&                    -- sbnmaster..users
insert into &audit_log& (...)            -- sbnwork..audit_log
select database = '&db-users&'           -- 'sbnmaster'
```

---

## Option File Hierarchy

Options are loaded in precedence order (later files override earlier):

```
{SQL_SOURCE}/CSS/Setup/
├── options.def           # 1. Default values (REQUIRED)
├── options.{company}     # 2. Company overrides (REQUIRED)
├── options.{company}.{profile}  # 3. Profile overrides (OPTIONAL)
└── table_locations       # 4. Table mappings (REQUIRED)
```

### Precedence Example

```
# options.def
v:timeout <<30>> Default timeout

# options.101
v:timeout <<60>> Company 101 needs longer timeout

# options.101.GONZO
v:timeout <<120>> GONZO server is slow
```

Result: `&timeout&` = `120`

---

## File Format Reference

### Value Option (v:)

```
v:name <<value>> description
```

**Examples:**
```
v:dbtbl <<sbnmaster>> Main tables database
v:dbpro <<sbnpro>> Stored procedures database
v:dbwrk <<sbnwork>> Work tables database
v:dbsta <<sbnstatic>> Static reference data
v:dbibs <<ibsmaster>> IBS framework database
v:af2 <<2>> Decimal places for money
v:cmpy <<101>> Company number
```

### Conditional Option (c:)

```
c:name +/- description
```

- `+` - Enabled (if blocks active, ifn blocks commented)
- `-` - Disabled (if blocks commented, ifn blocks active)

**Examples:**
```
c:mssql - SQL Server platform
c:sybase + Sybase platform
c:temp127 - Temporary company 127 workaround
```

### Table Location (->)

```
-> tablename &dbvar& description
```

**Examples:**
```
-> users &dbtbl& User master table
-> invoices &dbtbl& Invoice headers
-> options &dbibs& Runtime configuration
-> report_queue &dbwrk& Report job queue
```

### Comments

```
# This is a comment line
```

---

## Database Placeholder Reference

### Standard Databases

| Placeholder | Typical Value | Purpose |
|-------------|---------------|---------|
| `&dbtbl&` | sbnmaster | Main application tables |
| `&dbpro&` | sbnpro | Stored procedures |
| `&dbwrk&` | sbnwork | Temporary/work tables |
| `&dbsta&` | sbnstatic | Static reference data |
| `&dbibs&` | ibsmaster | IBS framework tables |

### Permission Placeholders

| Placeholder | Purpose |
|-------------|---------|
| `&tblauth&` | Table permissions (GRANT/REVOKE) |
| `&tbluser&` | Table user/role |
| `&proauth&` | Procedure permissions |
| `&prouser&` | Procedure user/role |

### Profile Placeholders

| Placeholder | Source | Purpose |
|-------------|--------|---------|
| `&cmpy&` | settings.json COMPANY | Company number |
| `&lang&` | settings.json DEFAULT_LANGUAGE | Language code |

---

## Sequence Processing

### Purpose

Run the same SQL file multiple times with different sequence numbers.

### Usage

```bash
# Run sequences 1 through 5
runsql script.sql sbnmaster GONZO -F 1 -L 5
```

### In SQL

```sql
-- @sequence@ replaced with current sequence number
insert into batch_log (batch_num) values (@sequence@)
```

### Conditional Blocks

```sql
@1@
-- Only runs when sequence = 1
create table temp_batch_1 (...)
@1@

@2@
-- Only runs when sequence = 2
insert into temp_batch_1 select * from source_2
@2@
```

---

## Caching

### Cache Location

```
{SQL_SOURCE}/CSS/Setup/temp/{profile}.options.tmp
```

### Cache Format

Simple key=value pairs:
```
&dbtbl&=sbnmaster
&dbpro&=sbnpro
&users&=sbnmaster..users
&db-users&=sbnmaster
&if_mssql&=/*
&endif_mssql&=*/
...
```

### Cache TTL

24 hours. After expiration, options are rebuilt from source files.

### Force Rebuild

Delete the cache file:
```bash
rm {SQL_SOURCE}/CSS/Setup/temp/GONZO.options.tmp
```

---

## Troubleshooting

### Missing Required File

```
ERROR: Required file 'options.def' not found.
Search context:
  Location: C:\_innovative\_source\current.sql\CSS\Setup
  Company:  101
  Profile:  GONZO
```

**Solution:** Ensure `{SQL_SOURCE}/CSS/Setup/options.def` exists.

### Unresolved Placeholder

If `&placeholder&` appears in output:
1. Check spelling in option files
2. Verify option file hierarchy loaded correctly
3. Check cache isn't stale (delete and rebuild)

### Conditional Block Not Working

1. Is `c:feature` defined in option files?
2. Is it `+` (enabled) or `-` (disabled)?
3. Spelling must match exactly (case-sensitive)

### Table Location Not Resolving

```
WARNING: Could not resolve database variable &dbxxx& in -> option
```

**Solution:** Define the database variable before using it in table locations:
```
v:dbxxx <<mydatabase>> My database
-> mytable &dbxxx& My table
```
