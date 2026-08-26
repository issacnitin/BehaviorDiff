'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const path = require('node:path');
const { createScope, readList } = require('../src/scope.cjs');

test('parses comma and semicolon separated scopes', () => {
  assert.deepEqual(readList('src/app; src/lib,src/app'), ['src/app', 'src/lib']);
});

test('include matches exact path segments and descendants', () => {
  const scope = createScope({ include: 'src/app,src/lib', exclude: 'src/app/generated' }, {});
  assert.equal(scope.selects(path.join(process.cwd(), 'src/app/service.js')), true);
  assert.equal(scope.selects(path.join(process.cwd(), 'src/lib.js')), false);
  assert.equal(scope.selects(path.join(process.cwd(), 'src/application/service.js')), false);
});

test('exclude wins for exact paths and descendants', () => {
  const scope = createScope({ include: 'src/app', exclude: 'src/app/generated' }, {});
  assert.equal(scope.selects(path.join(process.cwd(), 'src/app/generated/model.js')), false);
  assert.equal(scope.selects(path.join(process.cwd(), 'src/app/generatedElsewhere.js')), true);
});

test('scope is required', () => {
  assert.throws(() => createScope({}, {}), /requires an include scope/);
});