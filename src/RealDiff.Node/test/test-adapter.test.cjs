'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const adaptJest = require('../adapters/jest.cjs');
const { createRuntime, RUNTIME_SYMBOL } = require('../src/runtime.cjs');

function lines(filename) {
  const text = fs.readFileSync(filename, 'utf8').trim();
  return text ? text.split('\n').map(line => JSON.parse(line)) : [];
}

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'realdiff-adapter-'));
  const runtime = createRuntime({ tracePath: path.join(directory, 'run.ndjson'), processId: 23 });
  const previousRuntime = globalThis[RUNTIME_SYMBOL];
  globalThis[RUNTIME_SYMBOL] = runtime;
  t.after(() => {
    runtime.shutdown();
    if (previousRuntime === undefined) delete globalThis[RUNTIME_SYMBOL];
    else globalThis[RUNTIME_SYMBOL] = previousRuntime;
    fs.rmSync(directory, { recursive: true, force: true });
  });

  const metadata = {
    assembly: 'src/subject.js',
    methodFullName: 'src/subject.js#work',
    filePath: 'src/subject.js',
    filePathResolution: 'debugInfo',
    line: 1
  };
  runtime.registerModule({
    assembly: metadata.assembly,
    members: [{
      methodFullName: metadata.methodFullName,
      status: 'Patched',
      returnKind: 'Sync',
      sourceResolution: 'debugInfo'
    }]
  });
  return { runtime, metadata };
}

function fakeFramework() {
  const registrations = [];

  function register(mode) {
    const registration = function fakeTest(name, callbackOrOptions, possibleCallback) {
      const callback = typeof callbackOrOptions === 'function' ? callbackOrOptions : possibleCallback;
      registrations.push({
        mode,
        name,
        callback,
        callbackArgs: ['framework-argument'],
        context: { name }
      });
      return `${mode}:${name}`;
    };
    registration.each = rows => function generatedTest(name, callback) {
      for (const row of rows) {
        registrations.push({
          mode,
          name,
          callback,
          callbackArgs: Array.isArray(row) ? row : [row],
          context: { name, row }
        });
      }
      return `${mode}:${name}`;
    };
    return registration;
  }

  const frameworkTest = register('test');
  frameworkTest.only = register('only');
  frameworkTest.skip = register('skip');
  frameworkTest.todo = function todo(name) {
    registrations.push({ mode: 'todo', name });
    return `todo:${name}`;
  };
  return { frameworkTest, registrations };
}

function execute(registration) {
  return Reflect.apply(registration.callback, registration.context, registration.callbackArgs);
}

test('Jest adapter opens one root and preserves callback context, arguments, and return', t => {
  const { runtime, metadata } = fixture(t);
  const fake = fakeFramework();
  const wrapped = adaptJest(fake.frameworkTest);
  let callbackContext;
  let callbackArguments;

  assert.equal(wrapped.only('keeps semantics', function callback(...args) {
    callbackContext = this;
    callbackArguments = args;
    return runtime.runSync(metadata, args, () => 42);
  }), 'only:keeps semantics');

  const registration = fake.registrations[0];
  assert.equal(execute(registration), 42);
  assert.equal(callbackContext, registration.context);
  assert.deepEqual(callbackArguments, registration.callbackArgs);
  runtime.shutdown();

  const events = lines(runtime.tracePath);
  assert.equal(events.filter(event => event.isHarness === true).length, 1);
  assert.ok(events.every(event => event.testId === 'jest:keeps semantics'));
  assert.equal(events.find(event => !event.isHarness).callDepth, 1);
});

test('Vitest adapter keeps the root open until async settlement', async t => {
  const { runtime, metadata } = fixture(t);
  const fake = fakeFramework();
  const { default: adaptVitest } = await import('../adapters/vitest.mjs');
  const wrapped = adaptVitest(fake.frameworkTest);
  let settle;
  const pending = new Promise(resolve => { settle = resolve; });

  wrapped('waits', { timeout: 1000 }, async function callback() {
    await pending;
    return runtime.runSync(metadata, [], () => 'settled');
  });
  const returned = execute(fake.registrations[0]);
  assert.equal(fs.readFileSync(runtime.tracePath, 'utf8'), '');
  settle();
  assert.equal(await returned, 'settled');
  runtime.shutdown();

  const events = lines(runtime.tracePath);
  assert.equal(events.filter(event => event.isHarness === true).length, 1);
  assert.ok(events.every(event => event.testId === 'vitest:waits'));
});

test('skip and todo registrations do not execute callbacks or open roots', t => {
  const { runtime } = fixture(t);
  const fake = fakeFramework();
  const wrapped = adaptJest(fake.frameworkTest);
  let executed = false;

  assert.equal(wrapped.skip('disabled', () => { executed = true; }), 'skip:disabled');
  assert.equal(wrapped.todo('later'), 'todo:later');
  assert.equal(executed, false);
  assert.deepEqual(fake.registrations.map(item => item.mode), ['skip', 'todo']);
  runtime.shutdown();
  assert.deepEqual(lines(runtime.tracePath), []);
});

test('each cases get separate stable roots and manifest counts root invocations', t => {
  const { runtime, metadata } = fixture(t);
  const fake = fakeFramework();
  const wrapped = adaptJest(fake.frameworkTest);

  assert.equal(wrapped.each([[1, 'one'], [2, 'two']])('case %s', function callback(number, text) {
    return runtime.runSync(metadata, [number, text], () => `${number}:${text}`);
  }), 'test:case %s');
  assert.deepEqual(fake.registrations.map(execute), ['1:one', '2:two']);
  runtime.shutdown();

  const events = lines(runtime.tracePath);
  const roots = events.filter(event => event.isHarness === true);
  assert.equal(roots.length, 2);
  assert.equal(new Set(roots.map(root => root.testId)).size, 2);
  assert.ok(roots.every(root => root.testId.startsWith('jest:case %s#case=sha256:')));
  for (const root of roots) {
    assert.ok(events.find(event => event.parentCallId === root.callId && event.testId === root.testId));
  }

  const manifest = lines(runtime.manifestPath);
  const harnessAssembly = manifest.find(record =>
    record.kind === 'assembly' && record.assembly === '(node-harness)');
  const harnessMembers = manifest.filter(record =>
    record.kind === 'member' && record.assembly === '(node-harness)');
  assert.equal(harnessAssembly.tracedCalls, 2);
  assert.equal(harnessMembers.length, 1);
  assert.equal(harnessMembers[0].isTestRoot, true);
});