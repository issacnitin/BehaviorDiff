'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { createSourceResolver } = require('../src/source-map.cjs');
const { transform } = require('../src/transform.cjs');

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'behaviordiff-map-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.mkdirSync(path.join(root, 'dist'));
  return { root, filename: path.join(root, 'dist', 'work.js') };
}

test('direct JavaScript retains its generated debug information', t => {
  const { root, filename } = fixture(t);
  const resolver = createSourceResolver('function work() {}', filename, { repositoryRoot: root });
  assert.equal(resolver.mapState, 'direct');
  assert.deepEqual(resolver.resolve(3, 4), {
    resolution: 'debugInfo', filePath: 'dist/work.js', line: 3, column: 4
  });
});

test('external maps resolve sourceRoot relative to the map file', t => {
  const { root, filename } = fixture(t);
  fs.writeFileSync(`${filename}.map`, JSON.stringify({
    version: 3,
    sourceRoot: '../source',
    sources: ['work.ts'],
    names: [],
    mappings: 'AAGE'
  }));
  const source = '[1].map(function () {});\n//# sourceMappingURL=work.js.map';
  const resolver = createSourceResolver(
    source,
    filename,
    { repositoryRoot: root }
  );
  assert.equal(resolver.mapState, 'mapped');
  assert.deepEqual(resolver.resolve(1, 0), {
    resolution: 'debugInfo', filePath: 'source/work.ts', line: 4, column: 2
  });
  const output = transform(source, filename, { repositoryRoot: root, sourceResolver: resolver });
  assert.equal(output.members[0].methodFullName, 'source/work.ts#<anonymous@4:2>');
  assert.equal(output.members[0].line, 4);
  assert.equal(output.members[0].column, 2);
});

test('inline base64 and JSON maps resolve original positions', t => {
  const { root, filename } = fixture(t);
  const map = JSON.stringify({ version: 3, sources: ['../source/work.ts'], names: [], mappings: 'AAAA' });
  for (const encoded of [
    `data:application/json;base64,${Buffer.from(map).toString('base64')}`,
    `data:application/json,${encodeURIComponent(map)}`
  ]) {
    const resolver = createSourceResolver(`function work() {}\n//# sourceMappingURL=${encoded}`, filename, {
      repositoryRoot: root
    });
    assert.deepEqual(resolver.resolve(1, 0), {
      resolution: 'debugInfo', filePath: 'source/work.ts', line: 1, column: 0
    });
  }
});

test('missing malformed and unmappable maps remain unresolved without guessing', t => {
  const { root, filename } = fixture(t);
  const sources = [
    'function work() {}\n//# sourceMappingURL=missing.js.map',
    'function work() {}\n//# sourceMappingURL=data:application/json;base64,not-json'
  ];
  fs.writeFileSync(`${filename}.map`, JSON.stringify({
    version: 3, sources: ['../source/work.ts'], names: [], mappings: ''
  }));
  sources.push('function work() {}\n//# sourceMappingURL=work.js.map');
  for (const source of sources) {
    const resolved = createSourceResolver(source, filename, { repositoryRoot: root }).resolve(1, 0);
    assert.deepEqual(resolved, { resolution: 'unresolved', line: 0, column: 0 });
    assert.equal('filePath' in resolved, false);
  }
});