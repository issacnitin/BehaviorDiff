import testAdapter from '../src/test-adapter.cjs';

const { createTestAdapter } = testAdapter;

export function adaptVitest(testFunction) {
  return createTestAdapter(testFunction, 'vitest');
}

export default adaptVitest;