'use strict';

const path = require('node:path');
const { expect, test: jestTest } = require('@jest/globals');
const { asyncWork, work } = require('../src/subject.js');

const nodeRoot = process.env.BEHAVIORDIFF_NODE_ROOT
  || path.resolve(__dirname, '../../../src/BehaviorDiff.Node');
const adaptJest = require(path.join(nodeRoot, 'adapters/jest.cjs'));
const test = adaptJest(jestTest);

test('work calls the leaf synchronously', () => {
  expect(work(3)).toBe(7);
});

test('asyncWork resolves through work', async () => {
  await expect(asyncWork(4)).resolves.toBe(9);
});

test.each([[5, 11]])('work(%i) returns %i', (input, expected) => {
  expect(work(input)).toBe(expected);
});