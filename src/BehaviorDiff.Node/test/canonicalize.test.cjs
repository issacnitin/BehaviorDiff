'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { canonicalize } = require('../src/canonicalize.cjs');

test('does not invoke getters, toString, or iterators', () => {
  const calls = { getter: 0, toString: 0, iterator: 0, arrayGetter: 0 };
  const value = {
    get dangerous() {
      calls.getter++;
      throw new Error('getter ran');
    },
    toString() {
      calls.toString++;
      throw new Error('toString ran');
    },
    [Symbol.iterator]() {
      calls.iterator++;
      throw new Error('iterator ran');
    }
  };
  const array = [];
  Object.defineProperty(array, '0', {
    enumerable: true,
    get() {
      calls.arrayGetter++;
      throw new Error('array getter ran');
    }
  });
  value.array = array;

  const result = canonicalize(value);

  assert.deepEqual(calls, { getter: 0, toString: 0, iterator: 0, arrayGetter: 0 });
  assert.match(result.rendered, /<skipped:accessor>/);
  assert.match(result.rendered, /<skipped:Function>/);
  assert.equal(result.partial, true);
  assert.ok(result.counters.blocklisted >= 4);
});

test('detects proxies before any reflective operation', () => {
  const calls = { get: 0, ownKeys: 0, descriptor: 0, prototype: 0 };
  const proxy = new Proxy({}, {
    get() {
      calls.get++;
      throw new Error('get trap ran');
    },
    ownKeys() {
      calls.ownKeys++;
      throw new Error('ownKeys trap ran');
    },
    getOwnPropertyDescriptor() {
      calls.descriptor++;
      throw new Error('descriptor trap ran');
    },
    getPrototypeOf() {
      calls.prototype++;
      throw new Error('prototype trap ran');
    }
  });

  const result = canonicalize(proxy);

  assert.deepEqual(calls, { get: 0, ownKeys: 0, descriptor: 0, prototype: 0 });
  assert.equal(result.rendered, '<skipped:Proxy>');
  assert.equal(result.counters.blocklisted, 1);
});

test('distinguishes scalar types and exceptional numeric values', () => {
  const values = [
    undefined,
    null,
    NaN,
    Infinity,
    -Infinity,
    -0,
    0,
    1n,
    '1',
    Symbol.for('one')
  ];
  const results = values.map(value => canonicalize(value));

  assert.equal(new Set(results.map(result => result.digest)).size, values.length);
  assert.deepEqual(results.map(result => result.rendered), [
    'undefined',
    'null',
    'number:NaN',
    'number:+Infinity',
    'number:-Infinity',
    'number:-0',
    'number:0',
    'bigint:1n',
    'string:"1"',
    'symbol:global:"one"'
  ]);
});

test('preserves cycles and shared-reference topology', () => {
  const cycle = { name: 'root' };
  cycle.self = cycle;
  const child = { value: 7 };
  const shared = { left: child, right: child };
  const duplicated = { left: { value: 7 }, right: { value: 7 } };

  const cycleResult = canonicalize(cycle);
  const sharedResult = canonicalize(shared);
  const duplicatedResult = canonicalize(duplicated);

  assert.match(cycleResult.rendered, /<ref:0>/);
  assert.match(sharedResult.rendered, /<ref:1>/);
  assert.doesNotMatch(duplicatedResult.rendered, /<ref:1>/);
  assert.notEqual(sharedResult.digest, duplicatedResult.digest);
});

test('sorts string and symbol keys and retains symbol identity', () => {
  const symbolA = Symbol.for('a');
  const symbolZ = Symbol.for('z');
  const first = {};
  first.z = 1;
  first[symbolZ] = 2;
  first.a = 3;
  first[symbolA] = 4;
  const second = {};
  second[symbolA] = 4;
  second.a = 3;
  second[symbolZ] = 2;
  second.z = 1;

  const firstResult = canonicalize(first);
  const secondResult = canonicalize(second);

  assert.equal(firstResult.digest, secondResult.digest);
  assert.ok(firstResult.rendered.indexOf('string:"a"') < firstResult.rendered.indexOf('string:"z"'));
  assert.ok(firstResult.rendered.indexOf('symbol:global:"a"') < firstResult.rendered.indexOf('symbol:global:"z"'));

  const localA = Symbol('same');
  const localB = Symbol('same');
  const symbols = canonicalize([localA, localB, localA]);
  assert.match(symbols.rendered, /symbol:local:0/);
  assert.match(symbols.rendered, /symbol:local:1/);
  assert.equal(symbols.rendered.match(/symbol:local:0/g).length, 2);
});

test('uses own indexed array descriptors and distinguishes holes', () => {
  const hole = canonicalize([, 'value']);
  const explicitUndefined = canonicalize([undefined, 'value']);

  assert.match(hole.rendered, /length=2/);
  assert.doesNotMatch(hole.rendered, /string:"0"/);
  assert.match(explicitUndefined.rendered, /string:"0"=.*undefined/);
  assert.notEqual(hole.digest, explicitUndefined.digest);
});

test('marks graph and prototype depth limits', () => {
  const nested = canonicalize({ child: { value: 1 } }, { maxDepth: 1 });
  const prototype3 = Object.create(null);
  const prototype2 = Object.create(prototype3);
  const prototype1 = Object.create(prototype2);
  const instance = Object.create(prototype1);
  const prototypeLimited = canonicalize(instance, { maxPrototypeDepth: 1 });

  assert.match(nested.rendered, /<depth:Object>/);
  assert.equal(nested.counters.depthLimited, 1);
  assert.equal(nested.partial, true);
  assert.match(prototypeLimited.rendered, /<depth:prototype>/);
  assert.equal(prototypeLimited.counters.depthLimited, 1);
});

test('hashes full text before applying the rendered cap', () => {
  const value = { message: 'abcdefghijklmnopqrstuvwxyz' };
  const short = canonicalize(value, { renderedCap: 12 });
  const long = canonicalize(value, { renderedCap: 200 });

  assert.equal(short.digest, long.digest);
  assert.equal(short.rendered.endsWith('<truncated>'), true);
  assert.equal(short.counters.renderedTruncated, 1);
  assert.equal(short.partial, true);
  assert.equal(long.counters.renderedTruncated, 0);
});

test('marks deterministically omitted entries', () => {
  const result = canonicalize({ a: 1, b: 2, c: 3 }, { maxEntries: 2 });

  assert.match(result.rendered, /<skipped:entries:1>/);
  assert.equal(result.counters.blocklisted, 1);
  assert.equal(result.partial, true);
});

test('skips Maps and Sets without reading iterators', () => {
  const calls = { map: 0, set: 0 };
  const map = new Map([['key', 'value']]);
  const set = new Set(['value']);
  map[Symbol.iterator] = () => {
    calls.map++;
    throw new Error('map iterator ran');
  };
  set[Symbol.iterator] = () => {
    calls.set++;
    throw new Error('set iterator ran');
  };

  const result = canonicalize({ map, set });

  assert.deepEqual(calls, { map: 0, set: 0 });
  assert.match(result.rendered, /<skipped:Map>/);
  assert.match(result.rendered, /<skipped:Set>/);
  assert.equal(result.counters.blocklisted, 2);
  assert.equal(result.partial, true);
});

test('redacts names and credential-shaped content without changing compared digests', () => {
  const first = ['first-password', 'eyJabcdefghijk.abcdefghijkl.abcdefghijkl'];
  const second = ['second-password', 'eyJzyxwvutsrqp.zzyyxxwwvvu.abcdefghijkl'];
  const firstDigest = canonicalize(first);
  const secondDigest = canonicalize(second);
  const options = { redact: true, parameterNames: ['password', 'result'] };
  const firstRendered = canonicalize(first, options);
  const secondRendered = canonicalize(second, options);

  assert.notEqual(firstDigest.digest, secondDigest.digest);
  assert.doesNotMatch(firstRendered.rendered, /first-password|eyJabcdefghijk/);
  assert.doesNotMatch(secondRendered.rendered, /second-password|eyJzyxwvutsrqp/);
  assert.match(firstRendered.rendered, /<redacted>/);
  assert.match(secondRendered.rendered, /<redacted>/);
});