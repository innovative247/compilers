# check_api_procs

## Purpose

A deploy check for the API3 procedures a consumer (for example sbn-anywhere) depends on. It runs from a developer machine against a deployed APIEngine over HTTPS (plain http is accepted only for localhost), needs no database access, and compares the engine's live API3 maps with a recorded declaration of what the consumer was built against. The decision is handbook ADR-0019. The declaration format is owned by `.docs/sbn-ecosystem/api3-proc-declaration.md` in the development-handbook repo; this document does not restate it.

## Modes

Check (default):

```
check_api_procs <engine-url> --declaration <path> [--proc X]... [--json|--quiet] [--concurrency n] [--timeout s]
```

Record:

```
check_api_procs <engine-url> --record --names <file> --consumer <name> --release <version> --out <path>
```

`--record` reads a hand-kept names list (`--names`), fetches each proc's live map, and writes a recorded declaration to `--out`. `--declaration`, `--proc`, `--json` and `--quiet` are check-mode only; `--names`, `--consumer`, `--release` and `--out` require `--record`. `--json` and `--quiet` cannot be combined. Defaults: `--concurrency 8`, `--timeout 30` (seconds per request). `--proc` may repeat and must name a declared proc.

`--declaration` and `--names` take a local file path only. Downloading the declaration by consumer and release version is planned.

## Credentials

`SBN_USERNAME` and `SBN_PASSWORD` from the environment only: the developer's own engine login. No flags, no prompts. Missing either is a usage error (exit 2). The password is redacted from all output.

## Engine calls

Read-only. In order:

1. `GET /api/v1/diagnostic/version`
2. `POST /auth` (token sent as `User-Token`)
3. `GET /api/v1/api3/list`
4. `GET /api/v1/api3/map/<proc>` per proc, at most `--concurrency` at a time

Build and execute endpoints are never called. A map request can make the engine build and cache a map that does not exist yet. A 5xx response is retried once for a GET only; `POST /auth` is never retried.

Name matching: the list reports names truncated to 32 characters, so a declared name is looked up in the list by its first 32 characters (case-insensitive). The map request uses the full name.

## Verdicts

Per proc, in precedence order (first that applies wins):

| Verdict | Meaning | Result |
|---|---|---|
| `MAP-ERROR` | The map request failed. | fail |
| `MISSING` | Not in the engine's list. | fail |
| `PARAM-DIFF` | A recorded parameter is removed, or differs in type, default or output, or the relative order changed; or the engine requires a parameter the declaration lacks. An extra optional engine parameter is fine, anywhere. | fail |
| `MAP-STALE` | The cached map is outdated (proc compiled after it was mapped). A map that was never built (empty `createdOn` in the list) is not stale; the map request builds it. | warn |
| `RESULT-DIFF` | A recorded result set is missing, or its columns differ. Extra live result sets are ignored. | warn |
| `OK` | No difference. | pass |

Types are compared case-insensitively with whitespace removed. An unquoted `null` default is compared case-insensitively; quoted literals are case-sensitive. `MAP-STALE` prints a hint: refresh the map with `remap.py` or `GET /api/v1/api3/build/<proc>`. The checker runs neither.

## Record mode

All or nothing. It exits 1 and writes nothing when a declared name is missing from the list, a map request errors, or a reference map is stale (a stale map would record an out-of-date signature). A map that was never built (empty `createdOn` in the list) is not stale; the map request builds it. Each cause is reported on stderr. On success it writes the file and prints `recorded <n> procs from <url> (engine <version>) to <path>`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Pass; warnings allowed. Also `-h`/`--help`. |
| 1 | Any `MAP-ERROR`, `MISSING` or `PARAM-DIFF`; or a record-mode failure. |
| 2 | Usage error, unreadable or invalid declaration, no procedures, unsupported `formatVersion` major, or a `--proc` not declared. |
| 3 | Connectivity, auth or list failure. |
