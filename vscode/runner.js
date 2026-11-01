'use strict';
// Task runner: runner.js <kind> <diagFile|-> <diagLine> <batchOffset> <command> [args...]
// Runs the compiler with -O to a temp file and echoes that file to the terminal (so the compiler keeps the
// terminal for prompts), then prints one "<file>:<line>:1: error: ..." line per server error for $compilers.
const fs = require('fs');
const os = require('os');
const path = require('path');
const { StringDecoder } = require('string_decoder');
const { spawn } = require('child_process');
const { createScanner, formatProblem } = require('./lib/core');

const [kind, diagFile, diagLine, batchOffset, command, ...args] = process.argv.slice(2);
const tmp = path.join(os.tmpdir(), `compilers-out-${process.pid}-${Date.now()}.txt`);
// runcreate writes <outfile>.out (and creates <outfile>.err); the others write the file itself.
const outPath = kind === 'create' ? tmp + '.out' : tmp;

const scanner = createScanner();
const decoder = new StringDecoder('utf8');
const problems = [];
let offset = 0;

function report(errors) {
  for (const e of errors) problems.push(e);
}

function drain() {
  let size;
  try {
    size = fs.statSync(outPath).size;
  } catch (e) {
    return; // not created yet, or removed by the compiler during argument parsing
  }
  if (size < offset) offset = 0;
  if (size === offset) return;
  const buf = Buffer.alloc(size - offset);
  let fd;
  try {
    fd = fs.openSync(outPath, 'r');
    fs.readSync(fd, buf, 0, buf.length, offset);
  } catch (e) {
    return;
  } finally {
    if (fd !== undefined) fs.closeSync(fd);
  }
  offset = size;
  process.stdout.write(buf);
  report(scanner.push(decoder.write(buf)));
}

const child = spawn(command, [...args, '-O', tmp], { stdio: 'inherit' });
const timer = setInterval(drain, 100);
for (const sig of ['SIGINT', 'SIGHUP', 'SIGTERM']) process.on(sig, () => child.kill());

function finish(code) {
  clearInterval(timer);
  drain();
  report(scanner.push(decoder.end()));
  report(scanner.end());
  if (diagFile !== '-') {
    for (const e of problems) {
      const file = e.script || diagFile;
      process.stdout.write(formatProblem(e, file, e.script ? 1 : Number(diagLine), e.script ? 0 : Number(batchOffset)) + '\n');
    }
  }
  for (const f of [tmp, tmp + '.out', tmp + '.err']) {
    try { fs.unlinkSync(f); } catch (e) { /* not there */ }
  }
  process.exit(code);
}

child.on('error', (e) => {
  const why = e.code === 'ENOENT' ? `${command} was not found. Is Compilers installed and on PATH (or set compilers.installPath)?` : e.message;
  process.stdout.write(`Could not start ${command}: ${why}\n`);
  finish(127);
});
child.on('close', (code) => finish(code === null ? 1 : code));
