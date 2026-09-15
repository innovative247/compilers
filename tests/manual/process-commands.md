# Process Commands Test Cases (iwho, iplan, iplanext)

## Unix Equivalents

| Command | Unix Equivalent | Description |
|---------|----------------|-------------|
| `iwho` | `z` stored procedure | Show process list from sysprocesses |
| `iplan` | `iplan` shell script | Show execution plan for a SPID |
| `iplanext` | `iplanext` shell script | Extended: process info + SQL text + execution plan |

## iwho

### Test: List all processes
```bash
iwho GONZO
```
**Expected:** Table with columns: spid, status, login, hostname, blk, comm, cpu, io, host, proce. Should list all active processes.

### Test: Filter by username
```bash
iwho GONZO sbn0
```
**Expected:** Only processes where login = 'sbn0'.

### Test: Filter by username wildcard
```bash
iwho GONZO sbn%
```
**Expected:** Only processes where login LIKE 'sbn%'.

### Test: Filter by SPID
```bash
iwho GONZO 253
```
**Expected:** Single row for SPID 253 (if active). If SPID not active, header row only (no data).

### Test: Nonexistent SPID
```bash
iwho GONZO 99999
```
**Expected:** Header row only, no data rows.

---

## iplan

Calls `sp_showplan` (Sybase) or `sys.dm_exec_query_plan` (MSSQL) to show the execution plan for a running SPID.

### Test: Active SPID
```bash
iplan GONZO <active_spid>
```
**Expected:** SQL text of the running query PLUS the execution plan showing operators (Table Scan, Index Scan, MERGE JOIN, RESTRICT, EMIT, etc.), I/O sizes, buffer strategies. Must contain lines like:
- `QUERY PLAN FOR STATEMENT`
- `STEP 1`
- `The type of query is SELECT`
- `|ROOT:EMIT Operator`
- `|SCAN Operator`
- `FROM TABLE`

The plan operators are the critical output. If only SQL text appears without plan operators, the feature is broken (severity 10 PRINT messages from sp_showplan are being discarded).

### Test: Nonexistent SPID
```bash
iplan GONZO 222
```
**Expected:** Message from sp_showplan:
```
There is no active server process for the specified spid value '222'.  Possibly the user connection has terminated.
```
Should NOT output just blank/empty. The error messages come through as severity 10 PRINT messages.

### Test: Sleeping SPID
```bash
iplan GONZO <sleeping_spid>
```
**Expected:** Shows cached plan if process has one. May show SQL text + plan from last executed statement. If no cached plan, shows the "no active server process" message.

---

## iplanext

Combines three queries into one output file (PLANTRACE.<server>.<spid>.<pid>):
1. Process info (sysprocesses query)
2. SQL text (dbcc sqltext on Sybase, sys.dm_exec_sql_text on MSSQL)
3. Execution plan (sp_showplan on Sybase, sys.dm_exec_query_plan on MSSQL)

### Test: Active SPID
```bash
iplanext GONZO <active_spid>
```
**Expected output structure:**
```
<file_path>
<timestamp>
spid    status  login   hostname   blk  comm  cpu  io  host  proce
<spid>  <stat>  <user>  <host>     <b>  <cmd> <c>  <i> <hp>  <proc_name>
<SQL text from dbcc sqltext>
<execution plan from sp_showplan - must contain plan operators>
<file_path>
```

### Test: Nonexistent SPID
```bash
iplanext GONZO 222
```
**Expected:**
```
<file_path>
<timestamp>
There is no active server process for the specified spid value '222'.  Possibly the user connection has terminated.
(return status = 1)
There is no active server process for the specified spid value '222'.  Possibly the user connection has terminated.
There is no active server process for the specified spid value '222'.  Possibly the user connection has terminated.
<file_path>
```
Must NOT show just timestamp + (return status = 0). The "no active server process" message must appear.

Compare to Unix `iplanext` output:
```
/tmp/PLANTRACE.G.222.11600
Fri Feb  6 11:45:12 CST 2026
No login exists with the supplied name.
(return status = 1)
There is no active server process for the specified spid value '222'.
There is no active server process for the specified spid value '222'.
(return status = 1)
/tmp/PLANTRACE.G.222.11600
```
Note: Unix uses `z` proc which prints "No login exists". Our version uses a direct sysprocesses query so that specific message differs, but the sp_showplan messages must match.

---

## Technical Notes

### sp_showplan Output via tsql
On Sybase, `sp_showplan` outputs the execution plan via PRINT statements which tsql delivers as stderr messages with severity 10 (informational). The `execute_sql_native` function must use `include_info_messages=True` for iplan/iplanext to capture this output. Without it, only the SQL text (stdout) appears and the plan is silently discarded.

### Finding Test SPIDs
```bash
# Find active user processes
iwho GONZO
# Pick a sleeping or running SPID with a real login name
```
