'use strict';
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');
const core = require('./lib/core');

const KEY_PROFILE = 'compilers.profile';
const KEY_DB = 'compilers.database';
const KEY_RECENT = 'compilers.recentDatabases';

let ctx;
let statusProfile;
let statusDb;

const cfg = () => vscode.workspace.getConfiguration('compilers');
const exists = (p) => { try { return fs.statSync(p).isFile(); } catch (e) { return false; } };

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

function refreshStatus() {
  const profile = ctx.workspaceState.get(KEY_PROFILE);
  const db = ctx.workspaceState.get(KEY_DB);
  statusProfile.text = `$(database) ${profile || 'Compilers: pick profile'}`;
  statusDb.text = `$(symbol-namespace) ${db || 'pick database'}`;
  statusProfile.show();
  statusDb.show();
}

function updateContext() {
  const ed = vscode.window.activeTextEditor;
  const on = !!ed && core.matchesExtension(ed.document.fileName, cfg().get('fileExtensions'));
  vscode.commands.executeCommand('setContext', 'compilers.sbnFile', on);
}

async function pickProfile() {
  const { profiles, error } = loadProfiles();
  if (error || !profiles.length) {
    vscode.window.showErrorMessage(error || 'No profiles in the Compilers settings.json.');
    return undefined;
  }
  const current = ctx.workspaceState.get(KEY_PROFILE);
  const items = profiles.map((p) => ({
    label: p.name,
    description: p.aliases.length ? `aliases: ${p.aliases.join(', ')}` : '',
    detail: [p.platform, p.host].filter(Boolean).join('  '),
    picked: p.name === current,
    profile: p,
  }));
  const pick = await vscode.window.showQuickPick(items, { placeHolder: 'Compilers profile', matchOnDescription: true, matchOnDetail: true });
  if (!pick) return undefined;
  await ctx.workspaceState.update(KEY_PROFILE, pick.label);
  refreshStatus();
  return pick.label;
}

function pickDatabase() {
  const profile = ctx.workspaceState.get(KEY_PROFILE);
  const recent = (ctx.workspaceState.get(KEY_RECENT) || {})[profile] || [];
  const found = loadProfiles().profiles.find((p) => p.name === profile);
  const own = found && found.database ? [found.database] : [];
  const names = recent.concat(own.filter((n) => !recent.some((r) => r.toLowerCase() === n.toLowerCase())));
  return new Promise((resolve) => {
    const qp = vscode.window.createQuickPick();
    qp.placeholder = 'Database (pick, or type a name and press Enter)';
    const build = (typed) => {
      const items = names.map((n) => ({ label: n }));
      if (typed && !names.some((n) => n.toLowerCase() === typed.toLowerCase())) items.unshift({ label: typed, description: 'use this name' });
      qp.items = items;
    };
    build('');
    qp.onDidChangeValue(build);
    let settled = false;
    qp.onDidAccept(async () => {
      const chosen = qp.selectedItems[0] ? qp.selectedItems[0].label : qp.value.trim();
      settled = true;
      if (!chosen) { qp.hide(); return resolve(undefined); }
      const all = ctx.workspaceState.get(KEY_RECENT) || {};
      all[profile] = core.pushRecent(all[profile], chosen);
      await ctx.workspaceState.update(KEY_RECENT, all);
      await ctx.workspaceState.update(KEY_DB, chosen);
      refreshStatus();
      resolve(chosen);
      qp.hide();
    });
    qp.onDidHide(() => { qp.dispose(); if (!settled) resolve(undefined); });
    qp.show();
  });
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

function runTask(label, kind, o, diagFile, diagLine, cwd, batchOffset = 0) {
  const inv = core.buildInvocation(kind, o);
  const exe = resolveCommand(inv.command) || inv.command;
  const runner = path.join(ctx.extensionPath, 'runner.js');
  const execution = new vscode.ProcessExecution(
    process.execPath,
    [runner, kind, diagFile || '-', String(diagLine), String(batchOffset), exe, ...inv.args],
    { cwd, env: { ELECTRON_RUN_AS_NODE: '1' } }
  );
  const task = new vscode.Task({ type: 'compilers', kind }, vscode.TaskScope.Workspace, label, 'compilers', execution, '$compilers');
  task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, clear: true, panel: vscode.TaskPanelKind.Shared, focus: false };
  return vscode.tasks.executeTask(task);
}

function activeSqlEditor() {
  const ed = vscode.window.activeTextEditor;
  if (!ed) vscode.window.showWarningMessage('Open a SQL file first.');
  return ed;
}

async function runFile(uri) {
  let doc;
  if (uri instanceof vscode.Uri) {
    doc = await vscode.workspace.openTextDocument(uri);
  } else {
    const ed = activeSqlEditor();
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

async function runSelection() {
  const ed = activeSqlEditor();
  if (!ed) return;
  if (ed.selection.isEmpty) return vscode.window.showWarningMessage('Select the SQL to run.');
  const target = await ensureTarget();
  if (!target) return;
  const text = ed.document.getText(ed.selection);
  if (core.isToolSubcommand(text)) return vscode.window.showWarningMessage(`"${text.trim()}" would be read as a Compilers command, not SQL. Select more of the statement.`);
  if (core.selectionTooLong(text, process.platform)) return vscode.window.showWarningMessage('The selection is too long to run from the command line. Save it to a file and run the file instead.');
  const dir = ed.document.uri.scheme === 'file' ? path.dirname(ed.document.fileName) : undefined;
  await runTask(`isqlline selection -> ${target.database} on ${target.profile}`, 'selection',
    { text, ...target }, ed.document.uri.scheme === 'file' ? ed.document.fileName : '', ed.selection.start.line + 1, dir, 1);
}

/** F5: the selection when there is one, otherwise the whole file. */
async function run() {
  const ed = vscode.window.activeTextEditor;
  if (ed && !ed.selection.isEmpty) return runSelection();
  return runFile();
}

async function runCreate(uri) {
  const file = uri instanceof vscode.Uri ? uri.fsPath : (vscode.window.activeTextEditor || {}).document && vscode.window.activeTextEditor.document.fileName;
  if (!file) return vscode.window.showWarningMessage('Pick a create file first.');
  let profile = ctx.workspaceState.get(KEY_PROFILE) || (await pickProfile());
  if (!profile) return;
  const doc = vscode.workspace.textDocuments.find((d) => d.fileName === file);
  if (doc && doc.isDirty && !(await doc.save())) return;
  await runTask(`runcreate ${path.basename(file)} on ${profile}`, 'create', { file, profile }, file, 1, path.dirname(file));
}

function activate(context) {
  ctx = context;
  statusProfile = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 10);
  statusDb = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 9);
  statusProfile.command = 'compilers.pickProfile';
  statusDb.command = 'compilers.pickDatabase';
  statusProfile.tooltip = 'Compilers profile (click to change)';
  statusDb.tooltip = 'Compilers database (click to change)';
  refreshStatus();
  updateContext();
  core.sweepStale(os.tmpdir(), fs, Date.now(), 24 * 3600 * 1000);
  context.subscriptions.push(
    statusProfile, statusDb,
    vscode.commands.registerCommand('compilers.run', run),
    vscode.commands.registerCommand('compilers.runFile', runFile),
    vscode.commands.registerCommand('compilers.runSelection', runSelection),
    vscode.commands.registerCommand('compilers.runCreate', runCreate),
    vscode.commands.registerCommand('compilers.pickProfile', changeProfile),
    vscode.commands.registerCommand('compilers.pickDatabase', async () => {
      if (!ctx.workspaceState.get(KEY_PROFILE) && !(await pickProfile())) return;
      await pickDatabase();
    }),
    vscode.window.onDidChangeActiveTextEditor(updateContext),
    vscode.workspace.onDidChangeConfiguration((e) => { if (e.affectsConfiguration('compilers')) updateContext(); })
  );
}

function deactivate() {}

module.exports = { activate, deactivate };
