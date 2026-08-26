import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { expect, test as vitestTest } from 'vitest';
import subject from '../src/subject.js';

const currentDirectory = path.dirname(fileURLToPath(import.meta.url));
const nodeRoot = process.env.REALDIFF_NODE_ROOT
  || path.resolve(currentDirectory, '../../../src/RealDiff.Node');
const { default: adaptVitest } = await import(
  pathToFileURL(path.join(nodeRoot, 'adapters/vitest.mjs')).href
);
const importedTest = vitestTest.bind(undefined);
importedTest.each = (...tableArgs) => vitestTest.each(...tableArgs).bind(undefined);
const test = adaptVitest(importedTest);
const { asyncWork, work } = subject;

test('work calls the leaf synchronously', () => {
  expect(work(3)).toBe(7);
});

test('asyncWork resolves through work', async () => {
  await expect(asyncWork(4)).resolves.toBe(9);
});

test.each([[5, 11]])('work(%i) returns %i', (input, expected) => {
  expect(work(input)).toBe(expected);
});