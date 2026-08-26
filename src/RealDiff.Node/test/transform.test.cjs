'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const { transform } = require('../src/transform.cjs');
const { createSourceResolver } = require('../src/source-map.cjs');

function execute(source) {
  const events = [];
  const registrations = [];
  const context = { module: { exports: {} }, exports: {}, Symbol, Promise, setTimeout };
  context.globalThis = context;
  context[Symbol.for('realdiff.runtime')] = {
    registerModule(module) { registrations.push(module); },
    runSync(metadata, args, callback) {
      try {
        const value = callback();
        events.push({ metadata, args, value });
        return value;
      } catch (error) {
        events.push({ metadata, args, error });
        throw error;
      }
    },
    async runAsync(metadata, args, callback) {
      try {
        const value = await callback();
        events.push({ metadata, args, value });
        return value;
      } catch (error) {
        events.push({ metadata, args, error });
        throw error;
      }
    }
  };
  const output = transform(source, 'src/example.js');
  vm.runInNewContext(output.code, context);
  return { exports: context.module.exports, events, registrations, output };
}

test('uses runtime wrappers once for returns, fallthrough, and throws', () => {
  const run = execute('function add(a,b){return a+b} function empty(){} function fail(){throw new TypeError("bad")} module.exports={add,empty,fail};');
  assert.equal(run.exports.add(2, 3), 5);
  assert.equal(run.exports.empty(), undefined);
  assert.throws(() => run.exports.fail(), /bad/);
  assert.equal(run.events.length, 3);
  assert.equal(run.events[0].value, 5);
  assert.equal(run.events[2].error.name, 'TypeError');
  assert.equal(run.registrations.length, 1);
  assert.equal(run.registrations[0].members.length, 3);
  assert.deepEqual(run.output.members.map(member => member.methodFullName), [
    'src/example.js#add', 'src/example.js#empty', 'src/example.js#fail'
  ]);
});

test('preserves arrows, this, arguments, and lexical super', () => {
  const run = execute(`
    const twice=value=>value*2;
    const defaulted=(value=3)=>value;
    class Base { next(value) { return value + 1; } }
    class Counter extends Base { next(value) { return super.next(value) + this.offset; } }
    function first(){ return arguments[0]; }
    module.exports={twice,defaulted,Counter,first};
  `);
  const counter = new run.exports.Counter();
  counter.offset = 2;
  assert.equal(run.exports.twice(4), 8);
  assert.equal(run.exports.defaulted(), 3);
  assert.equal(counter.next(4), 7);
  assert.equal(run.exports.first('value'), 'value');
  assert.equal(run.events.length, 5);
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

test('returns manifest metadata for unsupported shapes and leaves them runnable', () => {
  const output = transform(
    'function* values(){yield 1} const pick=({value})=>value; class Child extends Object { constructor(){super()} }',
    'src/shapes.js'
  );
  assert.deepEqual(output.members.map(member => [member.status, member.skipReason, member.detail]), [
    ['Skipped', 'UnsupportedShape', 'Node: GeneratorFunction'],
    ['Skipped', 'UnsupportedShape', 'Node: DestructuredArrowParameters'],
    ['Skipped', 'UnsupportedShape', 'Node: DerivedConstructor']
  ]);
  assert.deepEqual(output.members.map(member => member.line), [1, 1, 1]);
  assert.ok(output.members.every(member => member.column >= 0));
});

test('records worker threads as an unsupported coverage boundary', () => {
  const output = transform("const { Worker } = require('node:worker_threads'); new Worker('./job.js');", 'src/workers.js');
  const worker = output.members.find(member => member.methodFullName.endsWith('#<worker_threads>'));
  assert.equal(worker.status, 'Skipped');
  assert.equal(worker.skipReason, 'UnsupportedShape');
  assert.equal(worker.detail, 'Node: WorkerThreadsOutOfScope');
});

test('anonymous identities include original line and column and bootstrap imports precede registration', () => {
  const output = transform('export default [1].map(function (value) { return value; });', 'src/module.mjs', {
    bootstrapImport: 'file:///runtime/bootstrap.mjs'
  });
  assert.match(output.members[0].methodFullName, /^src\/module\.mjs#<anonymous@1:\d+>$/);
  assert.match(output.code, /^import "file:\/\/\/runtime\/bootstrap\.mjs";/);
  assert.ok(output.code.indexOf('registerModule') < output.code.indexOf('export default'));
});

test('lexical identities distinguish object owners and retain nested ancestry', () => {
  const output = transform(`
    const left = { run() {} };
    const right = { run: () => {} };
    function outer() { return function middle() { return () => 1; }; }
  `, 'src/identities.js');
  assert.deepEqual(output.members.map(member => member.methodFullName), [
    'src/identities.js#left.run',
    'src/identities.js#right.run',
    'src/identities.js#outer.middle.<anonymous@4:57>',
    'src/identities.js#outer.middle',
    'src/identities.js#outer'
  ]);
});

test('mapped functions use original paths and coordinates for identity and registration', () => {
  const filename = path.join(process.cwd(), 'dist', 'compiled.js');
  const source = 'const first = function () {};\nconst second = () => {};\n';
  const map = {
    version: 3,
    sources: ['../source/first.ts', '../source/second.ts'],
    names: [],
    mappings: 'AAGE;ACAA'
  };
  const inline = `data:application/json;base64,${Buffer.from(JSON.stringify(map)).toString('base64')}`;
  const mappedSource = `${source}//# sourceMappingURL=${inline}`;
  const output = transform(mappedSource, filename, {
    sourceResolver: createSourceResolver(mappedSource, filename)
  });

  assert.deepEqual(output.members.map(member => ({
    methodFullName: member.methodFullName,
    sourceResolution: member.sourceResolution,
    line: member.line,
    column: member.column
  })), [{
    methodFullName: 'source/first.ts#first', sourceResolution: 'debugInfo', line: 4, column: 2
  }, {
    methodFullName: 'source/second.ts#second', sourceResolution: 'debugInfo', line: 4, column: 2
  }]);
  assert.deepEqual(output.modules.map(module => module.assembly), ['source/first.ts', 'source/second.ts']);
  assert.equal(output.code.match(/registerModule/g).length, 2);
});

test('unresolved maps omit source paths and use zero coordinates', () => {
  const filename = path.join(process.cwd(), 'dist', 'compiled.js');
  const source = 'const work = () => {};\n//# sourceMappingURL=missing.js.map';
  const output = transform(source, filename, {
    sourceResolver: createSourceResolver(source, filename)
  });

  assert.equal(output.members[0].sourceResolution, 'unresolved');
  assert.equal(output.members[0].line, 0);
  assert.doesNotMatch(output.code, /filePath:/);
  assert.match(output.members[0].methodFullName, /^dist\/compiled\.js#work$/);
});