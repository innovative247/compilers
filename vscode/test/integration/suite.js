'use strict';
// Runs inside the VS Code started by run.js. Live runs use COMPILERS_IT_PROFILE / COMPILERS_IT_DATABASE.
const assert = require('assert');
const path = require('path');
const vscode = require('vscode');

const MODE = process.env.COMPILERS_IT_MODE;
const DIR = process.env.COMPILERS_IT_DIR;
const PROFILE = process.env.COMPILERS_IT_PROFILE;
const DATABASE = process.env.COMPILERS_IT_DATABASE;
const INSIDE = path.join(DIR, 'ws', 'inside.sql');
const OUTSIDE = path.join(DIR, 'outside', 'outside.sql');

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function waitFor(what, fn, ms = 15000) {
  const end = Date.now() + ms;
  for (;;) {
    const v = await fn();
    if (v) return v;
    if (Date.now() > end) throw new Error(`timed out waiting for ${what}`);
    await sleep(100);
  }
}

let api;
const ext = () => vscode.extensions.getExtension('innovative247.compilers');
const logSince = (n) => api.log.slice(n).join('\n');

async function setTarget(profile, database, recent) {
  await api.state.update('compilers.profile', profile);
  await api.state.update('compilers.database', database);
  await api.state.update('compilers.recentDatabases', recent);
  api.refreshStatus();
}

async function open(file, select) {
  const doc = await vscode.workspace.openTextDocument(file);
  const ed = await vscode.window.showTextDocument(doc);
  ed.selection = select || new vscode.Selection(0, 0, 0, 0);
  return ed;
}

async function accept(qp) {
  await sleep(200);
  await vscode.commands.executeCommand('workbench.action.acceptSelectedQuickOpenItem');
}

/** Runs compilers.run and returns the log it wrote; asserts the task ended with exit 0. */
async function runAndExpectSuccess(label) {
  const n = api.log.length;
  await vscode.commands.executeCommand('compilers.run');
  const out = logSince(n);
  assert.match(out, /task start: /, `no task start in the log:\n${out}`);
  assert.match(out, new RegExp(`task end: "${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}" exit=0`), `task did not end with exit 0:\n${out}`);
  return out;
}

const tests = [];
const test = (name, fn, mode) => { if (!mode || mode === MODE) tests.push([name, fn]); };

test('opening a .sql file activates the extension (onLanguage:sql)', async () => {
  await open(MODE === 'folder' ? INSIDE : OUTSIDE);
  assert.strictEqual(vscode.window.activeTextEditor.document.languageId, 'sql');
  await waitFor('activation', () => ext().isActive);
  api = ext().exports;
  assert.ok(api && api.log, 'test hook not exported (COMPILERS_TEST_HOOK)');
  assert.match(api.log.join('\n'), /activated/);
});

test('status bar prompts for the profile and database while they are unset', async () => {
  await setTarget(undefined, undefined, undefined);
  assert.match(api.status().profile, /pick a profile/);
  assert.match(api.status().database, /pick a database/);
});

test('F5 resolves to compilers.run (after the debugger F5), and compilers.run skips the terminal shell', async () => {
  await vscode.commands.executeCommand('workbench.action.openDefaultKeybindingsFile');
  const doc = await waitFor('default keybindings', () => {
    const ed = vscode.window.activeTextEditor;
    return ed && /keybindings/i.test(ed.document.uri.toString()) && ed.document;
  });
  const json = doc.getText().split(/\r?\n/).filter((l) => !/^\s*\/\//.test(l)).join('\n');
  const rules = JSON.parse(json);
  const f5 = rules.map((r, i) => ({ ...r, i })).filter((r) => r.key === 'f5');
  const ours = f5.filter((r) => r.command === 'compilers.run');
  const debug = f5.find((r) => r.command === 'workbench.action.debug.start');
  assert.strictEqual(ours.length, 1, `compilers.run F5 rule missing: ${JSON.stringify(f5)}`);
  assert.strictEqual(ours[0].when.replace(/'/g, ''),'compilers.sqlEditorActive || editorTextFocus && editorLangId == sql');
  assert.ok(debug, 'workbench.action.debug.start F5 rule not found');
  // The resolver takes the last matching rule; extension rules come after the built-ins.
  assert.ok(ours[0].i > debug.i, 'compilers.run must come after workbench.action.debug.start');
  const later = f5.filter((r) => r.i > ours[0].i && !r.command.startsWith('-'));
  assert.deepStrictEqual(later.map((r) => r.command), [], 'an F5 rule after ours would win');
  const skip = vscode.workspace.getConfiguration('terminal.integrated').get('commandsToSkipShell');
  assert.ok(skip.includes('compilers.run'), `commandsToSkipShell: ${JSON.stringify(skip)}`);
  await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
});

test('database picker, profile set and no recent databases: shows and takes a typed name', async () => {
  await setTarget('NO_DATABASE_PROFILE', undefined, undefined);
  const n = api.log.length;
  const done = vscode.commands.executeCommand('compilers.pickDatabase');
  const qp = await waitFor('database Quick Pick', () => api.quickPick());
  assert.strictEqual(qp.items.length, 0);
  qp.value = 'sbnmaster';
  await waitFor('typed item', () => qp.items.length === 1);
  await accept(qp);
  await done;
  assert.strictEqual(api.state.get('compilers.database'), 'sbnmaster');
  assert.deepStrictEqual(api.state.get('compilers.recentDatabases'), { NO_DATABASE_PROFILE: ['sbnmaster'] });
  assert.match(api.status().database, /sbnmaster/);
  assert.match(logSince(n), /compilers\.pickDatabase invoked[\s\S]*database pick: sbnmaster/);
}, 'folder');

test('first F5 with nothing set asks for profile, then database, then runs the file (exit 0)', async () => {
  await setTarget(undefined, undefined, undefined);
  await open(INSIDE);
  const n = api.log.length;
  const done = vscode.commands.executeCommand('compilers.run');
  const qp1 = await waitFor('profile Quick Pick', () => api.quickPick());
  const item = qp1.items.find((i) => i.label === PROFILE);
  assert.ok(item, `profile ${PROFILE} not offered`);
  qp1.activeItems = [item];
  await accept(qp1);
  const qp2 = await waitFor('database Quick Pick', () => api.quickPick() !== qp1 && api.quickPick());
  qp2.value = DATABASE;
  await waitFor('typed or listed database', () => qp2.items.some((i) => i.label.toLowerCase() === DATABASE.toLowerCase()));
  qp2.activeItems = [qp2.items.find((i) => i.label.toLowerCase() === DATABASE.toLowerCase())];
  await accept(qp2);
  await done;
  const out = logSince(n);
  assert.match(out, new RegExp(`task end: "runsql inside.sql -> ${DATABASE} on ${PROFILE}" exit=0`), out);
  assert.match(out, /run file: profile=\S+ database=\S+ tool=.*runsql/i);
  assert.match(out, /argv: \[/);
  assert.match(api.status().profile, new RegExp(PROFILE));
}, 'folder');

test('a selection runs with isqlline and its SQL text is not logged', async () => {
  await setTarget(PROFILE, DATABASE, undefined);
  await open(INSIDE, new vscode.Selection(0, 0, 0, 18));
  const out = await runAndExpectSuccess(`isqlline selection -> ${DATABASE} on ${PROFILE}`);
  assert.match(out, /run selection: .*selection=18 chars/);
  assert.doesNotMatch(out, /select 1 as inside/);
}, 'folder');

test('F5 follows the active SQL editor while focus is in the Output panel', async () => {
  await setTarget(PROFILE, DATABASE, undefined);
  await open(INSIDE);
  await vscode.commands.executeCommand('workbench.panel.output.focus');
  const out = await runAndExpectSuccess(`runsql inside.sql -> ${DATABASE} on ${PROFILE}`);
  assert.match(out, /F5 target: .*inside\.sql \(sql\)/);
}, 'folder');

test('a file outside the workspace folder runs (exit 0)', async () => {
  await setTarget(PROFILE, DATABASE, undefined);
  await open(OUTSIDE);
  const out = await runAndExpectSuccess(`runsql outside.sql -> ${DATABASE} on ${PROFILE}`);
  assert.match(out, /task scope: Workspace/);
}, 'folder');

test('a failing command is logged, not swallowed', async () => {
  await setTarget(PROFILE, DATABASE, undefined);
  await open(INSIDE);
  const conf = vscode.workspace.getConfiguration('compilers');
  const before = conf.inspect('installPath').globalValue;
  await conf.update('installPath', path.join(DIR, 'no-such-folder'), vscode.ConfigurationTarget.Global);
  try {
    const n = api.log.length;
    await vscode.commands.executeCommand('compilers.run');
    assert.match(logSince(n), /compilers\.run failed: Error: runsql was not found/);
  } finally {
    await conf.update('installPath', before, vscode.ConfigurationTarget.Global);
  }
}, 'folder');

test('no folder open: a loose .sql file runs (exit 0)', async () => {
  assert.strictEqual(vscode.workspace.workspaceFolders, undefined);
  await setTarget(PROFILE, DATABASE, undefined);
  await open(OUTSIDE);
  const out = await runAndExpectSuccess(`runsql outside.sql -> ${DATABASE} on ${PROFILE}`);
  assert.match(out, /task scope: Workspace/);
}, 'nofolder');

exports.run = async function () {
  const failed = [];
  for (const [name, fn] of tests) {
    let timer;
    try {
      await Promise.race([fn(), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('timed out after 120s')), 120000); })]);
      console.log(`  ok    ${name}`);
    } catch (e) {
      failed.push(name);
      console.log(`  FAIL  ${name}\n${e.stack}`);
    } finally {
      clearTimeout(timer);
    }
  }
  if (api) console.log(`--- Compilers output (${MODE}) ---\n${api.log.join('\n')}`);
  console.log(`${tests.length - failed.length}/${tests.length} passed (${MODE})`);
  if (failed.length) throw new Error(`${failed.length} failed`);
};
