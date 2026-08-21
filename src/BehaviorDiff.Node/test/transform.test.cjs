'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const vm = require('node:vm');
const { transform } = require('../src/transform.cjs');

function execute(source) {
  const events = [];
  const context = { module: { exports: {} }, exports: {}, Symbol, Promise, setTimeout };
  context.globalThis = context;
  context[Symbol.for('behaviordiff.runtime')] = {
    enter(metadata, args) { return { metadata, args }; },
    exit(frame, value, error) { events.push({ frame, value, error }); }
  };
  const output = transform(source, 'src/example.js');
  vm.runInNewContext(output.code, context);
  return { exports: context.module.exports, events, output };
}

test('emits once for returns, fallthrough, and throws', () => {
  const run = execute('function add(a,b){return a+b} function empty(){} function fail(){throw new TypeError("bad")} module.exports={add,empty,fail};');
  assert.equal(run.exports.add(2, 3), 5);
  assert.equal(run.exports.empty(), undefined);
  assert.throws(() => run.exports.fail(), /bad/);
  assert.equal(run.events.length, 3);
  assert.equal(run.events[0].value, 5);
  assert.equal(run.events[2].error.name, 'TypeError');
});

test('supports arrows and class methods', () => {
  const run = execute('const twice=value=>value*2; class Counter{next(value){return value+1}} module.exports={twice,Counter};');
  assert.equal(run.exports.twice(4), 8);
  assert.equal(new run.exports.Counter().next(4), 5);
  assert.equal(run.events.length, 2);
});

test('async functions emit only after settlement', async () => {
  const run = execute('async function waitFor(promise){return promise} module.exports={waitFor};');
  let resolve;
  const pending = new Promise(done => { resolve = done; });
  const returned = run.exports.waitFor(pending);
  assert.equal(run.events.length, 0);
  resolve('done');
  assert.equal(await returned, 'done');
  assert.equal(run.events.length, 1);
  assert.equal(run.events[0].value, 'done');
});

test('records generators and destructured arrows as unsupported', () => {
  const output = transform('function* values(){yield 1} const pick=({value})=>value;', 'src/shapes.js');
  assert.deepEqual(output.unsupported.map(item => item.detail), ['Node: GeneratorFunction', 'Node: DestructuredArrowParameters']);
});