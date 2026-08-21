'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { createRuntime } = require('../src/runtime.cjs');

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'behaviordiff-node-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const runtime = createRuntime({ tracePath: path.join(directory, 'run.ndjson'), processId: 17 });
  const member = {
    methodFullName: 'src/example.js#work',
    status: 'Patched',
    returnKind: 'Sync',
    sourceResolution: 'debugInfo'
  };
  runtime.registerModule({ assembly: 'src/example.js', members: [member, {
    methodFullName: 'src/example.js#values',
    status: 'Skipped',
    skipReason: 'UnsupportedShape',
    detail: 'Node: GeneratorFunction',
    returnKind: 'Generator',
    sourceResolution: 'debugInfo'
  }] });
  const metadata = {
    assembly: 'src/example.js',
    methodFullName: member.methodFullName,
    filePath: 'src/example.js',
    filePathResolution: 'debugInfo',
    line: 4
  };
  return { runtime, metadata };
}

function lines(filename) {
  return fs.readFileSync(filename, 'utf8').trim().split('\n').map(line => JSON.parse(line));
}

test('writes schema-complete events and omits returns on throw', t => {
  const { runtime, metadata } = fixture(t);
  assert.equal(runtime.runSync(metadata, [2], () => 4), 4);
  assert.throws(() => runtime.runSync(metadata, [], () => { throw new TypeError('bad'); }), /bad/);
  runtime.shutdown();

  const events = lines(runtime.tracePath);
  assert.equal(events.length, 2);
  assert.deepEqual({
    testId: events[0].testId,
    methodFullName: events[0].methodFullName,
    filePath: events[0].filePath,
    filePathResolution: events[0].filePathResolution,
    line: events[0].line,
    callDepth: events[0].callDepth,
    ordinal: events[0].ordinal,
    threadId: events[0].threadId
  }, {
    testId: '(no-test)',
    methodFullName: 'src/example.js#work',
    filePath: 'src/example.js',
    filePathResolution: 'debugInfo',
    line: 4,
    callDepth: 0,
    ordinal: 0,
    threadId: 0
  });
  assert.ok(events[0].callId > 0);
  assert.match(events[0].argsDigest, /^sha256:/);
  assert.match(events[0].returnDigest, /^sha256:/);
  assert.equal(events[1].exceptionType, 'TypeError');
  assert.equal('returnDigest' in events[1], false);
  assert.equal(events[1].ordinal, 1);
});

test('async events wait for settlement and retain logical parents', async t => {
  const { runtime, metadata } = fixture(t);
  let resolve;
  const pending = new Promise(done => { resolve = done; });
  const returned = runtime.withTestRoot('case#1', () => runtime.runAsync(metadata, [], async () => {
    await pending;
    return runtime.runSync(metadata, ['child'], () => 'done');
  }));
  assert.equal(fs.readFileSync(runtime.tracePath, 'utf8'), '');
  resolve();
  assert.equal(await returned, 'done');
  runtime.shutdown();

  const events = lines(runtime.tracePath);
  assert.equal(events.length, 3);
  const child = events[0];
  const parent = events[1];
  const root = events[2];
  assert.equal(parent.parentCallId, root.callId);
  assert.equal(child.parentCallId, parent.callId);
  assert.deepEqual(events.map(event => event.callDepth), [2, 1, 0]);
  assert.ok(events.every(event => event.testId === 'case#1'));
  assert.equal(root.isHarness, true);
  assert.equal('isHarness' in parent, false);
});

test('manifest exactly reconciles modules, members, digests, and writer lines', t => {
  const { runtime, metadata } = fixture(t);
  runtime.runSync(metadata, [{ value: 1 }], () => true);
  runtime.shutdown();

  const events = lines(runtime.tracePath);
  const records = lines(runtime.manifestPath);
  assert.deepEqual(records[0], { kind: 'run', schema: 'behaviordiff.trace/1', language: 'node' });
  assert.equal(records.at(-1).kind, 'writer');
  assert.equal(records.at(-1).enqueued, events.length);
  assert.equal(records.at(-1).written, events.length);
  assert.equal(records.at(-1).dropped, 0);
  assert.ok(records.at(-1).capacity > 0);

  const module = records.find(record => record.kind === 'assembly');
  const members = records.filter(record => record.kind === 'member' && record.assembly === module.assembly);
  assert.equal(module.discovery, 'NodeAstTransform');
  assert.equal(module.discoveredMembers, module.patchedMembers + module.skippedMembers);
  assert.equal(members.length, module.discoveredMembers);
  assert.equal(module.tracedCalls, 1);
  assert.ok(records.find(record => record.kind === 'digest').valuesDigested > 0);
  assert.deepEqual(members.find(member => member.status === 'Skipped'), {
    kind: 'member',
    assembly: 'src/example.js',
    method: 'src/example.js#values',
    status: 'Skipped',
    returnKind: 'Generator',
    sourceResolution: 'debugInfo',
    skipReason: 'UnsupportedShape',
    detail: 'Node: GeneratorFunction'
  });
});