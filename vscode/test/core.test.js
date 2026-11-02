'use strict';
const test = require('node:test');
const assert = require('node:assert');
const path = require('path');
const c = require('../lib/core');

// Captured from `runsql t.sql master SRM_LOCAL` (MSSQL): the error is in the 2nd GO batch, which starts on file line 3.
const SAMPLE_MSSQL = [
  'Running C:\\x\\t.sql on SRM_LOCAL in master ',
  '             ',
  ' ----------- ',
  '           1 ',
  '',
  '(1 row affected)',
  'Msg 207, Level 16, State 1:',
  "Server 'IBS-JWILLIAMS\\DEV01', Line 1:",
  "Invalid column name 'bad_col'.",
  '',
].join('\n');

test('parseProfiles reads names, aliases, platform, case-insensitively, with BOM', () => {
  const text = '\uFEFF' + JSON.stringify({
    profiles: {
      SRM_LOCAL: { platform: 'MSSQL', HOST: '127.0.0.1', ALIASES: [], DATABASE: 'srm' },
      GONZO: { PLATFORM: 'SYBASE', HOST: 'h', ALIASES: ['G', 'GZ'], DATABASE: '' },
    },
  });
  const p = c.parseProfiles(text);
  assert.deepStrictEqual(p.map((x) => x.name), ['GONZO', 'SRM_LOCAL']);
  assert.deepStrictEqual(p[0].aliases, ['G', 'GZ']);
  assert.strictEqual(p[1].platform, 'MSSQL');
  assert.strictEqual(p[1].database, 'srm');
});

test('parseProfiles reads DATABASE in any casing, trimmed to a string, empty when absent or not text', () => {
  const p = c.parseProfiles(JSON.stringify({ Profiles: { A: { Database: 'srm' }, B: {}, C: { DATABASE: null }, D: { database: '' } } }));
  assert.deepStrictEqual(p.map((x) => x.database), ['srm', '', '', '']);
});

test('resolveDatabase: the pick wins, then the profile DATABASE, then nothing', () => {
  assert.deepStrictEqual(c.resolveDatabase('mine', 'srm'), { database: 'mine', source: 'pick' });
  assert.deepStrictEqual(c.resolveDatabase('', 'srm'), { database: 'srm', source: 'profile' });
  assert.deepStrictEqual(c.resolveDatabase(undefined, ' srm '), { database: 'srm', source: 'profile' });
  assert.deepStrictEqual(c.resolveDatabase('mine', ''), { database: 'mine', source: 'pick' });
  assert.deepStrictEqual(c.resolveDatabase('  ', ''), { database: '', source: '' });
  assert.deepStrictEqual(c.resolveDatabase(undefined, undefined), { database: '', source: '' });
});

test('pickedFor reads one profile out of the per-profile map', () => {
  const m = { SRM_LOCAL: 'a', GONZO: 'b' };
  assert.strictEqual(c.pickedFor(m, 'srm_local'), 'a');
  assert.strictEqual(c.pickedFor(m, 'GONZO'), 'b');
  assert.strictEqual(c.pickedFor(m, 'OTHER'), '');
  assert.strictEqual(c.pickedFor('legacy', 'GONZO'), '');
  assert.strictEqual(c.pickedFor(undefined, 'GONZO'), '');
});

test('parseProfiles tolerates bad input', () => {
  assert.deepStrictEqual(c.parseProfiles('not json'), []);
  assert.deepStrictEqual(c.parseProfiles('{}'), []);
  assert.deepStrictEqual(c.parseProfiles('{"Profiles":{"A":null}}')[0].aliases, []);
});

test('findOnPath honours PATHEXT-style extensions on Windows and plain names elsewhere', () => {
  const seen = new Set(['C:\\bin\\runsql.exe', '/usr/local/bin/runsql']);
  const ex = (p) => seen.has(p);
  assert.strictEqual(c.findOnPath('runsql', 'C:\\nope;C:\\bin', 'win32', ex), 'C:\\bin\\runsql.exe');
  assert.strictEqual(c.findOnPath('runsql', '/usr/bin:/usr/local/bin', 'linux', (p) => p === path.join('/usr/local/bin', 'runsql')), path.join('/usr/local/bin', 'runsql'));
  assert.strictEqual(c.findOnPath('runsql', 'C:\\nope', 'win32', ex), null);
});

test('settingsPathFor prefers beside the exe, then beside the symlink target', () => {
  const exe = path.join('/opt', 'link', 'runsql');
  const real = path.join('/opt', 'real', 'runsql');
  const target = path.join('/opt', 'real', 'settings.json');
  assert.strictEqual(c.settingsPathFor(exe, (p) => p === target, () => real), target);
  assert.strictEqual(c.settingsPathFor(exe, () => false, () => real), null);
});

test('buildInvocation passes selections verbatim (quotes, !, $, newlines, leading --)', () => {
  const sql = "-- c\nselect 'a!$b\"c', `x` from t where n = \"y\" and p like '%z%'";
  const inv = c.buildInvocation('selection', { text: sql, database: 'srm', profile: 'SRM_LOCAL' });
  assert.strictEqual(inv.command, 'isqlline');
  assert.deepStrictEqual(inv.args, [sql, 'srm', 'SRM_LOCAL']);
});

test('buildInvocation file and create', () => {
  assert.deepStrictEqual(c.buildInvocation('file', { file: 'C:\\a b\\x.sql', database: 'd', profile: 'P' }),
    { command: 'runsql', args: ['C:\\a b\\x.sql', 'd', 'P'] });
  assert.deepStrictEqual(c.buildInvocation('create', { file: '/x/create_all', profile: 'P' }),
    { command: 'runcreate', args: ['/x/create_all', 'P'] });
  assert.throws(() => c.buildInvocation('nope', {}));
});

test('matchesExtension and pushRecent', () => {
  assert.ok(c.matchesExtension('C:\\a\\X.SQL', ['.sql']));
  assert.ok(!c.matchesExtension('a.txt', ['.sql']));
  assert.deepStrictEqual(c.pushRecent(['a', 'b', 'c'], 'B'), ['B', 'a', 'c']);
  assert.deepStrictEqual(c.pushRecent(['a'], '  '), ['a']);
  assert.strictEqual(c.pushRecent(['1', '2', '3'], '4', 3).length, 3);
});

test('parseErrors reads the captured MSSQL sample', () => {
  const e = c.parseErrors(SAMPLE_MSSQL);
  assert.strictEqual(e.length, 1);
  assert.deepStrictEqual(e[0], {
    number: '207', level: 16, state: 1, server: 'IBS-JWILLIAMS\\DEV01', procedure: '', line: 1,
    message: "Invalid column name 'bad_col'.", script: 'C:\\x\\t.sql',
  });
});

test('parseErrors: procedure context, wrapped message, no server, several errors, CRLF, SQLSTATE', () => {
  const text = [
    'Msg 2714, Level 16, State 3:',
    "Server 'G', Procedure 'p_x', Line 12:",
    'There is already an object',
    'named p_x in the database.',
    '',
    'Msg 42703, Level 16, State 1:',
    'Line 4:',
    'column "q" does not exist',
  ].join('\r\n');
  const e = c.parseErrors(text);
  assert.strictEqual(e.length, 2);
  assert.strictEqual(e[0].procedure, 'p_x');
  assert.strictEqual(e[0].line, 12);
  assert.strictEqual(e[0].message, 'There is already an object named p_x in the database.');
  assert.strictEqual(e[1].number, '42703');
  assert.strictEqual(e[1].server, '');
  assert.strictEqual(e[1].message, 'column "q" does not exist');
});

test('parseErrors ignores ordinary output', () => {
  assert.deepStrictEqual(c.parseErrors('(1 row affected)\nMsg is a word\n'), []);
});

test('formatProblem never uses the batch line as the editor line, and the matcher regexp parses it', () => {
  const err = c.parseErrors(SAMPLE_MSSQL)[0];
  const file = 'C:\\_innovative\\x y\\t.sql';
  const line = c.formatProblem(err, file, 1);
  assert.strictEqual(line,
    "C:\\_innovative\\x y\\t.sql:1:1: error: Msg 207, Level 16: Invalid column name 'bad_col'. (Server 'IBS-JWILLIAMS\\DEV01', batch line 1)");
  const m = new RegExp(c.PROBLEM_RE).exec(line);
  assert.ok(m);
  assert.strictEqual(m[1], file);
  assert.strictEqual(m[2], '1');
  assert.strictEqual(m[4], 'error');
  // a deeper batch line must still be reported on the supplied editor line
  const deep = c.formatProblem({ ...err, line: 40 }, '/home/u/a.sql', 0);
  assert.match(deep, /^\/home\/u\/a\.sql:1:1: error:/);
  assert.match(deep, /batch line 40\)$/);
  assert.match(c.formatProblem({ ...err, procedure: 'p', line: 5 }, 'f', 7), /^f:7:1: .*procedure p line 5\)$/);
});

test('package.json problem matcher uses the same regexp as the code', () => {
  const pkg = require('../package.json');
  assert.strictEqual(pkg.contributes.problemMatchers[0].pattern.regexp, c.PROBLEM_RE);
});

// Captured from `runsql e.sql master SRM_LOCAL` (MSSQL): an exec that fails, a result set whose header starts with
// a space, then a second failure. Errors end at the blank line / leading-space header line.
const SAMPLE_EXEC = [
  'Running C:\\x\\e.sql on SRM_LOCAL in master ',
  'Msg 2812, Level 16, State 62:',
  "Server 'IBS-JWILLIAMS\\DEV01', Line 1:",
  "Could not find stored procedure 'no_such_proc_xyz'.",
  ' ok          ',
  ' ----------- ',
  '           2 ',
  '',
  '(1 row affected)',
  'Msg 259, Level 16, State 1:',
  "Server 'IBS-JWILLIAMS\\DEV01', Line 1:",
  'Ad hoc updates to system catalogs are not allowed.',
  '',
].join('\r\n');

test('parseErrors stops the message at a leading-space result header', () => {
  const e = c.parseErrors(SAMPLE_EXEC);
  assert.deepStrictEqual(e.map((x) => x.message), [
    "Could not find stored procedure 'no_such_proc_xyz'.",
    'Ad hoc updates to system catalogs are not allowed.',
  ]);
});

// Sybase layout (IsqlFormatter.ReturnStatus): the failed exec is followed directly by the return status line.
test('parseErrors stops at (return status), (N rows affected), Running and the next Msg', () => {
  const text = [
    'Msg 2812, Level 16, State 5:',
    "Server 'G', Line 1:",
    "Stored procedure 'nope' not found.",
    '(return status = -6)',
    'Msg 1, Level 11, State 1:',
    'Line 2:',
    'first',
    '(3 rows affected)',
    'Msg 2, Level 11, State 1:',
    'second',
    'Running C:\\s\\b.sql on G in db ',
  ].join('\n');
  const e = c.parseErrors(text);
  assert.deepStrictEqual(e.map((x) => x.message), ["Stored procedure 'nope' not found.", 'first', 'second']);
});

test('scanner works chunk by chunk and attributes errors to the script that was running', () => {
  const all = [
    'Running C:\\s\\one.sql on P in db ',
    'Msg 1, Level 16, State 1:',
    "Server 'S', Line 2:",
    'bad one',
    '',
    'Running C:\\s\\two dir\\two.sql on P in db ',
    'Msg 2, Level 16, State 1:',
    'bad two',
  ].join('\r\n');
  const sc = c.createScanner();
  const got = [];
  for (let i = 0; i < all.length; i += 7) got.push(...sc.push(all.slice(i, i + 7)));
  got.push(...sc.end());
  assert.deepStrictEqual(got.map((e) => [e.script, e.message]), [['C:\\s\\one.sql', 'bad one'], ['C:\\s\\two dir\\two.sql', 'bad two']]);
  assert.strictEqual(c.parseErrors('Msg 3, Level 16, State 1:\nx')[0].script, '');
});

test('formatProblem removes the blank line isqlline prepends from a selection batch line', () => {
  const err = { number: '8134', level: 16, state: 1, server: 'S', procedure: '', line: 3, message: 'Divide by zero error encountered.' };
  assert.match(c.formatProblem(err, 'f', 7, 1), /^f:7:1: error: .*\(Server 'S', batch line 2\)$/);
  assert.match(c.formatProblem({ ...err, line: 1 }, 'f', 7, 1), /encountered\.\s\(Server 'S'\)$|encountered\. \(Server 'S'\)$/);
});

test('isToolSubcommand matches what VersionCheck intercepts', () => {
  for (const w of ['update', ' Update ', '-v', '--help', '/install', 'configure', 'version', 'h', '?']) assert.ok(c.isToolSubcommand(w), w);
  for (const w of ['select 1', 'updates', 'update t set a = 1']) assert.ok(!c.isToolSubcommand(w), w);
});

test('selectionTooLong keeps clear of the Windows limit', () => {
  assert.ok(!c.selectionTooLong('x'.repeat(8000), 'win32'));
  assert.ok(c.selectionTooLong('x'.repeat(8001), 'win32'));
  assert.ok(!c.selectionTooLong('x'.repeat(8001), 'linux'));
});

test('findOnPath on Windows looks for .exe only', () => {
  const seen = [];
  c.findOnPath('runsql', 'C:\a', 'win32', (p) => { seen.push(p); return false; });
  assert.deepStrictEqual(seen, [path.join('C:\a', 'runsql.exe')]);
});

test('settingsPathFor prefers the real binary, then the found path, then cwd', () => {
  const calls = [];
  const exe = path.join('/l', 'runsql');
  const real = path.join('/r', 'runsql');
  assert.strictEqual(c.settingsPathFor(exe, (p) => { calls.push(p); return p === path.join('/cwd', 'settings.json'); }, () => real, '/cwd'), path.join('/cwd', 'settings.json'));
  assert.deepStrictEqual(calls, [path.join('/r', 'settings.json'), path.join('/l', 'settings.json'), path.join('/cwd', 'settings.json')]);
});

test('sweepStale removes only old compilers-out files', () => {
  const fs = require('fs');
  const os = require('os');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'compilers-sweeptest-'));
  try {
    const old = path.join(dir, 'compilers-out-1-1.txt.out');
    const fresh = path.join(dir, 'compilers-out-2-2.txt');
    const other = path.join(dir, 'other.txt');
    for (const f of [old, fresh, other]) fs.writeFileSync(f, '');
    const t = new Date(Date.now() - 3 * 86400000);
    fs.utimesSync(old, t, t);
    fs.utimesSync(other, t, t);
    assert.strictEqual(c.sweepStale(dir, fs, Date.now(), 86400000), 1);
    assert.deepStrictEqual(fs.readdirSync(dir).sort(), ['compilers-out-2-2.txt', 'other.txt']);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

// ---- matching profiles to a source folder ----

const P = (name, sqlSource) => ({ name, sqlSource });
const names = (l) => l.map((p) => p.name);

test('parseProfiles reads SQL_SOURCE in any casing, empty when absent', () => {
  const p = c.parseProfiles(JSON.stringify({ Profiles: { A: { SQL_SOURCE: ' C:\\src\\current.sql ' }, B: { sql_source: 'D:\\x' }, C: {} } }));
  assert.deepStrictEqual(p.map((x) => x.sqlSource), ['C:\\src\\current.sql', 'D:\\x', '']);
});

test('matchProfiles on Windows ignores case, slash style and trailing separators', () => {
  const l = [P('A', 'C:\\Src\\Current.SQL\\')];
  assert.deepStrictEqual(names(c.matchProfiles(l, 'c:/src/current.sql/proc/a.sql', 'win32')), ['A']);
  assert.deepStrictEqual(names(c.matchProfiles([P('A', 'c:/src/current.sql')], 'C:\\SRC\\CURRENT.SQL\\x.sql', 'win32')), ['A']);
  assert.deepStrictEqual(c.matchProfiles(l, 'D:\\src\\current.sql\\a.sql', 'win32'), []);
});

test('matchProfiles compares whole path segments: 95.sql does not match 95.sql2', () => {
  const l = [P('A', 'C:\\src\\95.sql')];
  assert.deepStrictEqual(c.matchProfiles(l, 'C:\\src\\95.sql2\\a.sql', 'win32'), []);
  assert.deepStrictEqual(names(c.matchProfiles(l, 'C:\\src\\95.sql\\a.sql', 'win32')), ['A']);
  assert.deepStrictEqual(names(c.matchProfiles(l, 'C:\\src\\95.sql', 'win32')), ['A']);
});

test('matchProfiles puts the deepest SQL_SOURCE first, then keeps the given order', () => {
  const l = [P('ROOT', 'C:\\src'), P('B', 'C:\\src\\95.sql'), P('DEEP', 'C:\\src\\95.sql\\proc'), P('B2', 'C:\\SRC\\95.sql')];
  assert.deepStrictEqual(names(c.matchProfiles(l, 'C:\\src\\95.sql\\proc\\a.sql', 'win32')), ['DEEP', 'B', 'B2', 'ROOT']);
});

test('matchProfiles never matches an empty SQL_SOURCE, an empty path or a relative path', () => {
  assert.deepStrictEqual(c.matchProfiles([P('RAW', ''), P('SP', '   ')], 'C:\\src\\a.sql', 'win32'), []);
  assert.deepStrictEqual(c.matchProfiles([P('A', 'C:\\src')], '', 'win32'), []);
  assert.deepStrictEqual(c.matchProfiles([P('A', 'C:\\src')], 'relative\\a.sql', 'win32'), []);
});

test('matchProfiles is case-sensitive on Linux (platform parameter)', () => {
  const l = [P('A', '/src/Current.sql/')];
  assert.deepStrictEqual(c.matchProfiles(l, '/src/current.sql/a.sql', 'linux'), []);
  assert.deepStrictEqual(names(c.matchProfiles(l, '/src/Current.sql//proc/a.sql', 'linux')), ['A']);
  assert.deepStrictEqual(c.matchProfiles([P('A', '/src/95.sql')], '/src/95.sql2/a.sql', 'linux'), []);
});
