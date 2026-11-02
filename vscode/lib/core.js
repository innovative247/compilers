'use strict';
// Pure logic for the Compilers extension. Must not import 'vscode' so it tests under plain node.
const path = require('path');

// ---- settings.json profiles -------------------------------------------------

function ci(obj, name) {
  if (!obj || typeof obj !== 'object') return undefined;
  const key = Object.keys(obj).find((k) => k.toLowerCase() === name.toLowerCase());
  return key === undefined ? undefined : obj[key];
}

/** Profiles from the text of the compilers settings.json (case-insensitive keys, as the compilers read it). */
function parseProfiles(text) {
  let json;
  try {
    json = JSON.parse(String(text).replace(/^﻿/, ''));
  } catch (e) {
    return [];
  }
  const profiles = ci(json, 'Profiles');
  if (!profiles || typeof profiles !== 'object') return [];
  return Object.keys(profiles)
    .map((name) => {
      const p = profiles[name] || {};
      const aliases = ci(p, 'ALIASES');
      return {
        name,
        aliases: Array.isArray(aliases) ? aliases.filter((a) => typeof a === 'string' && a) : [],
        platform: String(ci(p, 'PLATFORM') || ''),
        host: String(ci(p, 'HOST') || ''),
        database: String(ci(p, 'DATABASE') || ''),
        sqlSource: String(ci(p, 'SQL_SOURCE') || '').trim(),
      };
    })
    .sort((a, b) => a.name.localeCompare(b.name));
}

/**
 * The database to run against: the one picked for the profile, else the profile's DATABASE when non-empty, else
 * none (the caller opens the picker). source is 'pick', 'profile' or ''.
 */
function resolveDatabase(picked, profileDatabase) {
  const pick = String(picked || '').trim();
  if (pick) return { database: pick, source: 'pick' };
  const own = String(profileDatabase || '').trim();
  if (own) return { database: own, source: 'profile' };
  return { database: '', source: '' };
}

/** The user's pick for one profile out of the stored map (case-insensitive name). A legacy single string is ignored. */
function pickedFor(map, profile) {
  if (!map || typeof map !== 'object' || !profile) return '';
  const key = Object.keys(map).find((k) => k.toLowerCase() === String(profile).toLowerCase());
  return key === undefined ? '' : String(map[key] || '');
}

// ---- matching profiles to a source folder -----------------------------------

/**
 * Absolute path with one slash style, no trailing separator (except a root), lower-cased on Windows. '' when the
 * path is empty or not absolute for that platform. `platform` is process.platform's value; tests pass it.
 */
function normalizePath(p, platform) {
  const win = platform === 'win32';
  const api = win ? path.win32 : path.posix;
  const raw = String(p || '').trim();
  if (!raw || !api.isAbsolute(raw)) return '';
  let s = api.normalize(raw);
  const root = api.parse(s).root;
  while (s.length > root.length && s.endsWith(api.sep)) s = s.slice(0, -1);
  return win ? s.toLowerCase() : s;
}

/** True when `file` is `dir` or lies under it, compared by whole path segments. */
function isInside(file, dir, platform) {
  const f = normalizePath(file, platform);
  const d = normalizePath(dir, platform);
  if (!f || !d) return false;
  const sep = platform === 'win32' ? path.win32.sep : path.posix.sep;
  return f === d || f.startsWith(d.endsWith(sep) ? d : d + sep);
}

/**
 * Profiles whose SQL_SOURCE contains `file`: deepest SQL_SOURCE first, then the order of `profiles`. Profiles with
 * an empty SQL_SOURCE never match.
 */
function matchProfiles(profiles, file, platform) {
  const sep = platform === 'win32' ? '\\' : '/';
  return (profiles || [])
    .filter((p) => p.sqlSource && isInside(file, p.sqlSource, platform))
    .map((p, i) => ({ p, i, depth: normalizePath(p.sqlSource, platform).split(sep).length }))
    .sort((a, b) => b.depth - a.depth || a.i - b.i)
    .map((x) => x.p);
}

// ---- locating the install ---------------------------------------------------

/** First existing <dir>/<name>[ext] on PATH. exists(p) is injected for tests. */
function findOnPath(name, pathVar, platform, exists) {
  const win = platform === 'win32';
  const sep = win ? ';' : ':';
  const exts = win ? ['.exe'] : [''];
  for (const dir of String(pathVar || '').split(sep)) {
    if (!dir) continue;
    for (const ext of exts) {
      const p = path.join(dir.replace(/^"|"$/g, ''), name + ext);
      if (exists(p)) return p;
    }
  }
  return null;
}

/** settings.json as ProfileManager.FindSettingsFile finds it: beside the real binary, then beside the found path, then in cwd. */
function settingsPathFor(exePath, exists, realpath, cwd) {
  let real = exePath;
  try { real = realpath(exePath); } catch (e) { /* not a link */ }
  const dirs = [path.dirname(real), path.dirname(exePath)];
  if (cwd) dirs.push(cwd);
  for (const d of dirs) {
    const p = path.join(d, 'settings.json');
    if (exists(p)) return p;
  }
  return null;
}

// ---- command lines ----------------------------------------------------------

/** The compiler command and its argument vector; no shell is involved, so nothing is quoted. */
function buildInvocation(kind, o) {
  if (kind === 'file') return { command: 'runsql', args: [o.file, o.database, o.profile] };
  if (kind === 'selection') return { command: 'isqlline', args: [o.text, o.database, o.profile] };
  if (kind === 'create') return { command: 'runcreate', args: [o.file, o.profile] };
  throw new Error('unknown kind ' + kind);
}

// VersionCheck.CheckForUpdates treats a first argument matching one of these as a subcommand.
const SUBCOMMANDS = ['version', 'v', 'install', 'update', 'configure', 'help', 'h', '?'];
function isToolSubcommand(text) {
  return SUBCOMMANDS.includes(String(text).trim().toLowerCase().replace(/^[-/]+/, ''));
}

/** True when a selection is too long to pass as one command-line argument (Windows limit 32767, kept well clear). */
function selectionTooLong(text, platform) {
  return String(text).length > (platform === 'win32' ? 8000 : 100000);
}

function matchesExtension(fileName, exts) {
  const lower = String(fileName).toLowerCase();
  return (exts || []).some((e) => e && lower.endsWith(String(e).toLowerCase()));
}

/** Most-recent-first list without duplicates, capped. */
function pushRecent(list, value, max = 8) {
  const v = String(value).trim();
  if (!v) return list || [];
  return [v, ...(list || []).filter((x) => x.toLowerCase() !== v.toLowerCase())].slice(0, max);
}

// ---- server errors ----------------------------------------------------------
// IsqlFormatter.Error prints "Msg N, Level L, State S:", then "Server 'X'[, Procedure 'p'][, Line N]:", then the message.

const MSG_RE = /^Msg (\w+), Level (\d+), State (\d+):\s*$/;
const PART = "(?:Server '[^']*'|Procedure '[^']*'|Line \\d+)";
const CTX_RE = new RegExp('^(' + PART + '(?:, ' + PART + ')*):\\s*$');
const RUNNING_RE = /^Running (.+) on \S+ /;
const ENDS_MESSAGE_RE = /^(?:\s|\(\d+ rows? affected\)|\(return status = |Running |Msg )/;

/**
 * Incremental error scanner: push() text chunks as they arrive, end() when done. Each error carries `script`,
 * the path from the latest "Running <path> on ..." line (runcreate runs many scripts), or '' before any.
 */
function createScanner() {
  const errors = [];
  let tail = '';
  let script = '';
  let cur = null; // error being read
  let ctxOpen = false; // the line after "Msg" may be the context line
  const msg = [];

  function close() {
    if (cur) {
      cur.message = msg.join(' ');
      errors.push(cur);
    }
    cur = null;
    msg.length = 0;
  }

  function line(l) {
    if (cur) {
      const c = ctxOpen ? CTX_RE.exec(l) : null;
      ctxOpen = false;
      if (c) {
        const s = /Server '([^']*)'/.exec(c[1]);
        const p = /Procedure '([^']*)'/.exec(c[1]);
        const n = /Line (\d+)/.exec(c[1]);
        cur.server = s ? s[1] : '';
        cur.procedure = p ? p[1] : '';
        cur.line = n ? Number(n[1]) : 0;
        return;
      }
      if (l !== '' && !ENDS_MESSAGE_RE.test(l)) {
        msg.push(l.trim());
        return;
      }
      close();
    }
    const r = RUNNING_RE.exec(l);
    if (r) script = r[1];
    const m = MSG_RE.exec(l);
    if (m) {
      cur = { number: m[1], level: Number(m[2]), state: Number(m[3]), server: '', procedure: '', line: 0, message: '', script };
      ctxOpen = true;
    }
  }

  return {
    /** Feed text; returns the errors completed so far (and forgets them). */
    push(text) {
      let raw = tail + text;
      tail = '';
      if (raw.endsWith('\r')) { tail = '\r'; raw = raw.slice(0, -1); } // a CRLF may be split across chunks
      const parts = raw.replace(/\r\n?/g, '\n').split('\n');
      tail = parts.pop() + tail;
      parts.forEach(line);
      return errors.splice(0);
    },
    /** Flush the last line and any error still open. */
    end() {
      if (tail.replace(/\r$/, '') !== '') line(tail.replace(/\r$/, ''));
      tail = '';
      close();
      return errors.splice(0);
    },
  };
}

function parseErrors(text) {
  const s = createScanner();
  return s.push(text).concat(s.end());
}

/**
 * One line per error for the $compilers problem matcher: <file>:<line>:1: error: Msg N, Level L: <message> (...)
 * The server's Line N counts from the GO batch (or procedure), never from the file, so it only appears in the
 * text; the problem sits on `line`. `batchOffset` is the number of lines isqlline adds before a selection.
 */
function formatProblem(err, file, line, batchOffset = 0) {
  const where = err.procedure ? `procedure ${err.procedure}` : 'batch';
  const n = err.procedure ? err.line : err.line - batchOffset;
  const ctx = [err.server ? `Server '${err.server}'` : '', n > 0 ? `${where} line ${n}` : ''].filter(Boolean).join(', ');
  const sev = err.level >= 11 ? 'error' : 'warning';
  return `${file}:${line || 1}:1: ${sev}: Msg ${err.number}, Level ${err.level}: ${err.message}${ctx ? ` (${ctx})` : ''}`;
}

/** Delete compilers-out-* files in dir older than maxAgeMs. */
function sweepStale(dir, fs, now, maxAgeMs) {
  let removed = 0;
  let names = [];
  try { names = fs.readdirSync(dir); } catch (e) { return 0; }
  for (const n of names) {
    if (!n.startsWith('compilers-out-')) continue;
    const p = path.join(dir, n);
    try {
      if (now - fs.statSync(p).mtimeMs > maxAgeMs) { fs.unlinkSync(p); removed++; }
    } catch (e) { /* in use or gone */ }
  }
  return removed;
}

/** The regexp source contributed as problemMatcher $compilers (kept here so tests use the same one). */
const PROBLEM_RE = '^(.+?):(\\d+):(\\d+): (error|warning): (.*)$';

module.exports = {
  parseProfiles, normalizePath, isInside, matchProfiles, resolveDatabase, pickedFor, findOnPath, settingsPathFor, buildInvocation, matchesExtension, pushRecent,
  createScanner, parseErrors, formatProblem, isToolSubcommand, selectionTooLong, sweepStale, PROBLEM_RE,
};
