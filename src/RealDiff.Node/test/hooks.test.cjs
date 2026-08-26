'use strict';

const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { pathToFileURL } = require('node:url');

const packageRoot = path.resolve(__dirname, '..');

function readOnlyMatch(directory, pattern) {
  const matches = fs.readdirSync(directory).filter(name => pattern.test(name));
  assert.equal(matches.length, 1, `expected one ${pattern} file, found ${matches.join(', ')}`);
  return fs.readFileSync(path.join(directory, matches[0]), 'utf8').trim().split('\n').map(line => JSON.parse(line));
}

function runChild(t, kind) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), `realdiff-${kind}-`));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  fs.mkdirSync(path.join(directory, 'app'));
  const tracePath = path.join(directory, 'run.ndjson');
  const environment = {
    ...process.env,
    REALDIFF_TRACE: tracePath,
    REALDIFF_NAMESPACES: 'app'
  };

  let argumentsList;
  if (kind === 'cjs') {
    environment.REALDIFF_EXCLUDE_NAMESPACES = 'app/excluded.js';
    fs.writeFileSync(path.join(directory, 'app', 'subject.js'), 'exports.work = value => value * 2;\n');
    fs.writeFileSync(path.join(directory, 'app', 'excluded.js'), 'exports.hidden = () => 9;\n');
    fs.writeFileSync(path.join(directory, 'runner.cjs'), `
      const subject = require('./app/subject.js');
      require('./app/excluded.js').hidden();
      globalThis[Symbol.for('realdiff.runtime')].withTestRoot('cjs#1', () => subject.work(4));
    `);
    argumentsList = ['--require', path.join(packageRoot, 'register.cjs'), path.join(directory, 'runner.cjs')];
  } else {
    fs.writeFileSync(path.join(directory, 'app', 'subject.mjs'), 'export const work = async value => value + 1;\n');
    fs.writeFileSync(path.join(directory, 'runner.mjs'), `
      import { work } from './app/subject.mjs';
      await globalThis[Symbol.for('realdiff.runtime')].withTestRoot('esm#1', () => work(4));
    `);
    argumentsList = ['--loader', pathToFileURL(path.join(packageRoot, 'loader.mjs')).href,
      path.join(directory, 'runner.mjs')];
  }

  const result = childProcess.spawnSync(process.execPath, argumentsList, {
    cwd: directory,
    env: environment,
    encoding: 'utf8'
  });
  assert.equal(result.status, 0, result.stderr || result.stdout);
  return {
    events: readOnlyMatch(directory, /^run\.\d+\.ndjson$/),
    manifest: readOnlyMatch(directory, /^run\.\d+\.manifest\.ndjson$/)
  };
}

test('CommonJS require hook emits real events and excluded member records', t => {
  const run = runChild(t, 'cjs');
  assert.equal(run.events.length, 2);
  const subject = run.events.find(event => !event.isHarness);
  assert.equal(subject.testId, 'cjs#1');
  assert.equal(subject.filePath, 'app/subject.js');
  assert.equal(subject.parentCallId, run.events.find(event => event.isHarness).callId);
  const excluded = run.manifest.find(record =>
    record.kind === 'member' && record.method === 'app/excluded.js#exports.hidden');
  assert.equal(excluded.status, 'Skipped');
  assert.equal(excluded.skipReason, 'ExcludedByScope');
});

test('ESM loader bootstraps the runtime in the application realm', t => {
  const run = runChild(t, 'esm');
  assert.equal(run.events.length, 2);
  const subject = run.events.find(event => !event.isHarness);
  assert.equal(subject.testId, 'esm#1');
  assert.equal(subject.methodFullName, 'app/subject.mjs#work');
  assert.equal(subject.returnRendered, 'number:5');
  assert.ok(run.manifest.find(record => record.kind === 'assembly' && record.assembly === 'app/subject.mjs'));
});