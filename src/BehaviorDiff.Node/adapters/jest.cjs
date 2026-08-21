'use strict';

const { createTestAdapter } = require('../src/test-adapter.cjs');

function adaptJest(testFunction) {
  return createTestAdapter(testFunction, 'jest');
}

module.exports = adaptJest;
module.exports.adaptJest = adaptJest;