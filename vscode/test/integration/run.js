'use strict';
// Integration test: runs test/integration/suite.js inside a real VS Code, twice (a folder open, no folder open),
// each with its own --user-data-dir and --extensions-dir under a scratch folder, never the user's profile.
//
// Environment:
//   VSCODE_EXECUTABLE      Code.exe to run. Default: download the latest stable into vscode/.vscode-test.
//   COMPILERS_TEST_DIR     Scratch root. Default: a new folder in the system temp; removed when the run ends.
//   COMPILERS_IT_PROFILE   Profile for the live runs (default SRM_LOCAL). Must exist in the Compilers settings.json.
//   COMPILERS_IT_DATABASE  Database for the live runs (default master). The scripts only run "select".
// Needs runsql/isqlline on PATH (or COMPILERS_INSTALL_PATH, passed on as compilers.installPath).
const fs = require('fs');
const os = require('os');
const path = require('path');
const { runTests } = require('@vscode/test-electron');

const root = path.resolve(__dirname, '..', '..');
const ownDir = !process.env.COMPILERS_TEST_DIR;
const scratch = process.env.COMPILERS_TEST_DIR || fs.mkdtempSync(path.join(os.tmpdir(), 'compilers-vscode-it-'));

function write(file, text) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, text);
}

async function launch(mode) {
  const dir = path.join(scratch, mode);
  fs.rmSync(dir, { recursive: true, force: true });
  const ws = path.join(dir, 'ws');
  write(path.join(ws, 'inside.sql'), 'select 1 as inside\ngo\n');
  write(path.join(dir, 'outside', 'outside.sql'), 'select 2 as outside\ngo\n');
  if (process.env.COMPILERS_INSTALL_PATH) {
    write(path.join(dir, 'user-data', 'User', 'settings.json'), JSON.stringify({ 'compilers.installPath': process.env.COMPILERS_INSTALL_PATH }));
  }
  const args = [];
  if (mode === 'folder') args.push(ws);
  args.push(
    `--user-data-dir=${path.join(dir, 'user-data')}`,
    `--extensions-dir=${path.join(dir, 'extensions')}`,
    '--disable-extensions'
  );
  console.log(`\n=== integration (${mode}) ===`);
  await runTests({
    vscodeExecutablePath: process.env.VSCODE_EXECUTABLE || undefined,
    cachePath: path.join(root, '.vscode-test'),
    extensionDevelopmentPath: root,
    extensionTestsPath: path.join(__dirname, 'suite.js'),
    launchArgs: args,
    extensionTestsEnv: {
      COMPILERS_TEST_HOOK: '1',
      COMPILERS_IT_MODE: mode,
      COMPILERS_IT_DIR: dir,
      COMPILERS_IT_PROFILE: process.env.COMPILERS_IT_PROFILE || 'SRM_LOCAL',
      COMPILERS_IT_DATABASE: process.env.COMPILERS_IT_DATABASE || 'master',
    },
  });
}

(async () => {
  let code = 0;
  try {
    await launch('folder');
    await launch('nofolder');
  } catch (e) {
    console.error(`Integration tests failed: ${e.message}`);
    code = 1;
  } finally {
    if (ownDir) fs.rmSync(scratch, { recursive: true, force: true });
  }
  process.exit(code);
})();
