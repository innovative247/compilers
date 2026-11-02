'use strict';
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');
const core = require('./lib/core');

const KEY_PROFILE = 'compilers.profile';
const KEY_DB = 'compilers.database';
const KEY_RECENT = 'compilers.recentDatabases';
const CONTEXT_KEY = 'compilers.sqlEditorActive';
const TEST_HOOK = process.env.COMPILERS_TEST_HOOK === '1';
const START_TIMEOUT_MS = 20000;

let ctx;
let statusProfile;
let statusDb;
let output;
let taskSeq = 0;
let currentPick;
const testLog = [];

const cfg = () => vscode.workspace.getConfiguration('compilers');
const exists = (p) => { try { return fs.statSync(p).isFile(); } catch (e) { return false; } };

/** One line in the Compilers output channel. */
function log(msg) {
  const line = `[${new Date().toISOString()}] ${msg}`;
  output.appendLine(line);
  if (TEST_HOOK) testLog.push(line);
}

const errorText = (e) => (e && e.message ? e.message : String(e));

/** Registers a command whose failures are logged and shown, never swallowed. */
function command(id, fn) {
  return vscode.commands.registerCommand(id, async (...args) => {
    log(`${id} invoked`);
    try {
      return await fn(...args);
    } catch (e) {
      log(`${id} failed: ${(e && e.stack) || errorText(e)}`);
      vscode.window.showErrorMessage(`Compilers: ${errorText(e)}`, 'Show log').then((c) => c && output.show(true));
      return undefined;
    }
  });
}

/** Absolute path of a compiler command: compilers.installPath first, then PATH. null when not found. */
function resolveCommand(name) {
  const install = cfg().get('installPath');
  return core.findOnPath(name, install || process.env.PATH, process.platform, exists);
}

function loadProfiles() {
  const exe = resolveCommand('runsql');
  if (!exe) return { error: 'runsql was not found on PATH. Install Compilers or set "compilers.installPath".', profiles: [] };
  const settings = core.settingsPathFor(exe, exists, fs.realpathSync, process.cwd());
  if (!settings) return { error: `No settings.json next to ${exe}.`, profiles: [] };
  try {
    return { profiles: core.parseProfiles(fs.readFileSync(settings, 'utf8')) };
  } catch (e) {
    return { error: `Cannot read ${settings}: ${e.message}`, profiles: [] };
  }
}

/** Status bar: the profile and database, or a warning-coloured prompt for whichever is missing. */
function refreshStatus() {
  const profile = ctx.workspaceState.get(KEY_PROFILE);
  const db = ctx.workspaceState.get(KEY_DB);
  const warn = new vscode.ThemeColor('statusBarItem.warningBackground');
  statusProfile.text = profile ? `$(database) ${profile}` : '$(warning) Compilers: pick a profile';
  statusProfile.backgroundColor = profile ? undefined : warn;
  statusDb.text = db ? `$(symbol-namespace) ${db}` : '$(warning) Compilers: pick a database';
  statusDb.backgroundColor = db ? undefined : warn;
  statusProfile.show();
  statusDb.show();
}

function isScript(doc) {
  return doc.languageId === 'sql' || core.matchesExtension(doc.fileName, cfg().get('fileExtensions'));
}

/**
 * The text editor of the editor area's active tab, wherever keyboard focus is. activeTextEditor alone is not
 * enough: focusing the Output panel makes it the output document.
 */
function targetEditor() {
  const tab = vscode.window.tabGroups.activeTabGroup.activeTab;
  const input = tab && tab.input;
  const uri = input instanceof vscode.TabInputText ? input.uri : input instanceof vscode.TabInputTextDiff ? input.modified : undefined;
  if (uri) {
    const ed = vscode.window.visibleTextEditors.find((e) => e.document.uri.toString() === uri.toString());
    if (ed) return ed;
  }
  const ed = vscode.window.activeTextEditor;
  return ed && ed.document.uri.scheme !== 'output' ? ed : undefined;
}

/** Global context key behind F5: true while the active editor is a SQL/Compilers script, whatever has focus. */
function updateContext() {
  const ed = targetEditor();
  vscode.commands.executeCommand('setContext', CONTEXT_KEY, !!ed && isScript(ed.document));
}

/**
 * A Quick Pick that, with allowTyped, also takes a typed name. ignoreFocusOut keeps it open when focus moves
 * (a status-bar click, F5 from another part), so it never closes unseen. Resolves the label, or undefined.
 */
function pickOne({ title, placeholder, items, allowTyped }) {
  return new Promise((resolve) => {
    const qp = vscode.window.createQuickPick();
    qp.title = title;
    qp.placeholder = placeholder;
    qp.ignoreFocusOut = true;
    qp.matchOnDescription = true;
    qp.matchOnDetail = true;
    const build = (typed) => {
      const list = items.slice();
      if (allowTyped && typed && !items.some((i) => i.label.toLowerCase() === typed.toLowerCase())) {
        list.unshift({ label: typed, description: 'use this name', alwaysShow: true });
      }
      qp.items = list;
    };
    build('');
    const current = items.find((i) => i.picked);
    if (current) qp.activeItems = [current];
    if (allowTyped) qp.onDidChangeValue(build);
    let settled = false;
    const done = (value) => {
      if (settled) return;
      settled = true;
      resolve(value);
      qp.hide();
    };
    qp.onDidAccept(() => {
      const typed = qp.value.trim();
      const sel = qp.selectedItems[0] || qp.activeItems[0];
      done(sel ? sel.label : allowTyped && typed ? typed : undefined);
    });
    qp.onDidHide(() => {
      if (currentPick === qp) currentPick = undefined;
      qp.dispose();
      done(undefined);
    });
    currentPick = qp;
    qp.show();
  });
}

async function pickProfile() {
  const { profiles, error } = loadProfiles();
  if (error || !profiles.length) {
    const why = error || 'No profiles in the Compilers settings.json.';
    log(`profile list: ${why}`);
    vscode.window.showErrorMessage(why);
    return undefined;
  }
  const current = ctx.workspaceState.get(KEY_PROFILE);
  const items = profiles.map((p) => ({
    label: p.name,
    description: p.aliases.length ? `aliases: ${p.aliases.join(', ')}` : '',
    detail: [p.platform, p.host].filter(Boolean).join('  '),
    picked: p.name === current,
  }));
  const pick = await pickOne({ title: 'Compilers profile', placeholder: 'Pick the profile to run against', items });
  log(`profile pick: ${pick || '(cancelled)'}`);
  if (!pick) return undefined;
  await ctx.workspaceState.update(KEY_PROFILE, pick);
  refreshStatus();
  return pick;
}

async function pickDatabase() {
  const profile = ctx.workspaceState.get(KEY_PROFILE);
  const recent = (ctx.workspaceState.get(KEY_RECENT) || {})[profile] || [];
  const found = loadProfiles().profiles.find((p) => p.name === profile);
  const own = found && found.database ? [found.database] : [];
  const names = recent.concat(own.filter((n) => !recent.some((r) => r.toLowerCase() === n.toLowerCase())));
  const chosen = await pickOne({
    title: `Compilers database on ${profile}`,
    placeholder: names.length ? 'Pick a database, or type a name and press Enter' : 'Type a database name and press Enter',
    items: names.map((n) => ({ label: n })),
    allowTyped: true,
  });
  log(`database pick: ${chosen || '(cancelled)'}`);
  if (!chosen) return undefined;
  const all = ctx.workspaceState.get(KEY_RECENT) || {};
  all[profile] = core.pushRecent(all[profile], chosen);
  await ctx.workspaceState.update(KEY_RECENT, all);
  await ctx.workspaceState.update(KEY_DB, chosen);
  refreshStatus();
  return chosen;
}

async function changeProfile() {
  const before = ctx.workspaceState.get(KEY_PROFILE);
  const p = await pickProfile();
  if (p && p !== before) await pickDatabase();
}

/** Both targets, prompting for whichever is unset. undefined when the user cancels. */
async function ensureTarget() {
  let profile = ctx.workspaceState.get(KEY_PROFILE);
  if (!profile) profile = await pickProfile();
  if (!profile) return undefined;
  let database = ctx.workspaceState.get(KEY_DB);
  if (!database) database = await pickDatabase();
  if (!database) return undefined;
  return { profile, database };
}

/**
 * Task scope: the file's workspace folder, else TaskScope.Workspace, which also runs for a file outside every
 * folder and with no folder open (the integration test covers both).
 */
function taskScope(file) {
  const folder = file ? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(file)) : undefined;
  return folder || vscode.TaskScope.Workspace;
}

/**
 * How the task starts runner.js with VS Code's own executable as Node. On Windows a task terminal never starts
 * Code.exe itself (no process, no terminal, no error), so it goes through cmd.exe and runner.cmd, with the
 * arguments as JSON in an environment variable so cmd never parses the SQL text.
 */
function runnerExecution(runnerArgs, cwd) {
  if (process.platform === 'win32') {
    return new vscode.ProcessExecution(process.env.ComSpec || 'cmd.exe', ['/d', '/c', path.join(ctx.extensionPath, 'runner.cmd')], {
      cwd,
      env: { COMPILERS_NODE: process.execPath, COMPILERS_RUNNER_ARGS: JSON.stringify(runnerArgs) },
    });
  }
  return new vscode.ProcessExecution(process.execPath, [path.join(ctx.extensionPath, 'runner.js'), ...runnerArgs], {
    cwd,
    env: { ELECTRON_RUN_AS_NODE: '1' },
  });
}

/**
 * Runs one task and resolves { launched, exitCode } when it ends. launched is false when VS Code reported no
 * process: with no folder open, the first task of a window ends at once with no pid and no exit code.
 */
async function executeOnce(label, kind, scope, execution) {
  const id = `${process.pid}-${++taskSeq}`;
  const task = new vscode.Task({ type: 'compilers', kind, id }, scope, label, 'compilers', execution, '$compilers');
  task.presentationOptions = {
    reveal: vscode.TaskRevealKind.Always, focus: true, clear: true, panel: vscode.TaskPanelKind.Shared, showReuseMessage: false,
  };
  const mine = (e) => e.execution.task.definition.id === id;
  const subs = [];
  const finished = new Promise((resolve) => {
    let pid;
    const watchdog = setTimeout(() => {
      log(`task "${label}" did not start a process within ${START_TIMEOUT_MS / 1000}s`);
      resolve({ launched: false });
    }, START_TIMEOUT_MS);
    subs.push({ dispose: () => clearTimeout(watchdog) });
    subs.push(vscode.tasks.onDidStartTaskProcess((e) => {
      if (!mine(e)) return;
      pid = e.processId;
      if (pid !== undefined) clearTimeout(watchdog);
      log(`task start: "${label}" pid=${pid}`);
    }));
    subs.push(vscode.tasks.onDidEndTaskProcess((e) => {
      if (!mine(e)) return;
      log(`task end: "${label}" exit=${e.exitCode}`);
      resolve({ launched: pid !== undefined || e.exitCode !== undefined, exitCode: e.exitCode });
    }));
    subs.push(vscode.tasks.onDidEndTask((e) => {
      if (!mine(e) || pid !== undefined) return;
      log(`task "${label}" ended without starting a process`);
      resolve({ launched: false });
    }));
  });
  try {
    await vscode.tasks.executeTask(task);
    return await finished;
  } finally {
    subs.forEach((d) => d.dispose());
  }
}

/** Starts the compiler in a task and logs it; resolves with the exit code when it has ended. */
async function runTask(label, kind, o, diagFile, diagLine, cwd, batchOffset = 0) {
  const inv = core.buildInvocation(kind, o);
  const exe = resolveCommand(inv.command);
  if (!exe) throw new Error(`${inv.command} was not found on PATH. Install Compilers or set "compilers.installPath".`);
  const shownArgs = kind === 'selection' ? [`<selection, ${o.text.length} chars>`, ...inv.args.slice(1)] : inv.args;
  log(`run ${kind}: profile=${o.profile} database=${o.database || '-'} tool=${exe} ` +
    (kind === 'selection' ? `selection=${o.text.length} chars` : `file=${o.file}`));
  log(`argv: ${JSON.stringify([exe, ...shownArgs])} cwd=${cwd || '-'}`);
  const runnerArgs = [kind, diagFile || '-', String(diagLine), String(batchOffset), exe, ...inv.args];
  const scope = taskScope(diagFile);
  log(`task scope: ${typeof scope === 'number' ? 'Workspace' : scope.uri.fsPath}`);
  let r = await executeOnce(label, kind, scope, runnerExecution(runnerArgs, cwd || os.homedir()));
  if (!r.launched) {
    log('no process was started; trying once more');
    r = await executeOnce(label, kind, scope, runnerExecution(runnerArgs, cwd || os.homedir()));
  }
  if (!r.launched) throw new Error(`"${label}" did not start. See the Compilers output.`);
  return r.exitCode;
}

function sqlEditor() {
  const ed = targetEditor();
  if (!ed) vscode.window.showWarningMessage('Open a SQL file first.');
  return ed;
}

async function runFile(uri) {
  let doc;
  if (uri instanceof vscode.Uri) {
    doc = await vscode.workspace.openTextDocument(uri);
  } else {
    const ed = sqlEditor();
    if (!ed) return;
    doc = ed.document;
  }
  if (doc.isUntitled || doc.uri.scheme !== 'file') return vscode.window.showWarningMessage('Save the file to disk first.');
  const target = await ensureTarget();
  if (!target) return;
  if (doc.isDirty && !(await doc.save())) return;
  await runTask(`runsql ${path.basename(doc.fileName)} -> ${target.database} on ${target.profile}`, 'file',
    { file: doc.fileName, ...target }, doc.fileName, 1, path.dirname(doc.fileName));
}

async function runSelection(ed = sqlEditor()) {
  if (!ed) return;
  if (ed.selection.isEmpty) return vscode.window.showWarningMessage('Select the SQL to run.');
  const target = await ensureTarget();
  if (!target) return;
  const text = ed.document.getText(ed.selection);
  if (core.isToolSubcommand(text)) return vscode.window.showWarningMessage(`"${text.trim()}" would be read as a Compilers command, not SQL. Select more of the statement.`);
  if (core.selectionTooLong(text, process.platform)) return vscode.window.showWarningMessage('The selection is too long to run from the command line. Save it to a file and run the file instead.');
  const onDisk = ed.document.uri.scheme === 'file';
  await runTask(`isqlline selection -> ${target.database} on ${target.profile}`, 'selection',
    { text, ...target }, onDisk ? ed.document.fileName : '', ed.selection.start.line + 1, onDisk ? path.dirname(ed.document.fileName) : undefined, 1);
}

/** F5: the selection when there is one, otherwise the whole file. */
async function run() {
  const ed = sqlEditor();
  if (!ed) return;
  log(`F5 target: ${ed.document.fileName} (${ed.document.languageId}), selection=${ed.selection.isEmpty ? 'none' : 'yes'}`);
  if (!ed.selection.isEmpty) return runSelection(ed);
  return runFile(ed.document.uri);
}

async function runCreate(uri) {
  const ed = targetEditor();
  const file = uri instanceof vscode.Uri ? uri.fsPath : ed && ed.document.fileName;
  if (!file) return vscode.window.showWarningMessage('Pick a create file first.');
  const profile = ctx.workspaceState.get(KEY_PROFILE) || (await pickProfile());
  if (!profile) return;
  const doc = vscode.workspace.textDocuments.find((d) => d.fileName === file);
  if (doc && doc.isDirty && !(await doc.save())) return;
  await runTask(`runcreate ${path.basename(file)} on ${profile}`, 'create', { file, profile }, file, 1, path.dirname(file));
}

function activate(context) {
  ctx = context;
  output = vscode.window.createOutputChannel('Compilers');
  log(`Compilers ${context.extension.packageJSON.version} activated (VS Code ${vscode.version})`);
  statusProfile = vscode.window.createStatusBarItem('compilers.profile', vscode.StatusBarAlignment.Left, 10);
  statusDb = vscode.window.createStatusBarItem('compilers.database', vscode.StatusBarAlignment.Left, 9);
  statusProfile.name = 'Compilers profile';
  statusDb.name = 'Compilers database';
  statusProfile.command = 'compilers.pickProfile';
  statusDb.command = 'compilers.pickDatabase';
  statusProfile.tooltip = 'Compilers profile (click to change)';
  statusDb.tooltip = 'Compilers database (click to change)';
  refreshStatus();
  updateContext();
  core.sweepStale(os.tmpdir(), fs, Date.now(), 24 * 3600 * 1000);
  context.subscriptions.push(
    output, statusProfile, statusDb,
    command('compilers.run', run),
    command('compilers.runFile', runFile),
    command('compilers.runSelection', () => runSelection()),
    command('compilers.runCreate', runCreate),
    command('compilers.pickProfile', changeProfile),
    command('compilers.pickDatabase', async () => {
      if (!ctx.workspaceState.get(KEY_PROFILE) && !(await pickProfile())) return;
      await pickDatabase();
    }),
    vscode.window.onDidChangeActiveTextEditor(updateContext),
    vscode.window.onDidChangeVisibleTextEditors(updateContext),
    vscode.window.tabGroups.onDidChangeTabs(updateContext),
    vscode.window.tabGroups.onDidChangeTabGroups(updateContext),
    vscode.workspace.onDidOpenTextDocument(updateContext), // also fires when a document's language changes
    vscode.workspace.onDidChangeConfiguration((e) => { if (e.affectsConfiguration('compilers')) updateContext(); })
  );
  if (TEST_HOOK) {
    return {
      log: testLog,
      state: context.workspaceState,
      refreshStatus,
      status: () => ({ profile: statusProfile.text, database: statusDb.text }),
      quickPick: () => currentPick,
    };
  }
  return undefined;
}

function deactivate() {}

module.exports = { activate, deactivate };
