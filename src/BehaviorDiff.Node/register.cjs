'use strict';

const fs = require('node:fs');
const Module = require('node:module');
const { createScope } = require('./src/scope.cjs');
const { transform } = require('./src/transform.cjs');

const scope = createScope();
const original = Module._extensions['.js'];
Module._extensions['.js'] = function behaviorDiffExtension(module, filename) {
  if (!scope.selects(filename)) {
    return original(module, filename);
  }
  const source = fs.readFileSync(filename, 'utf8');
  const result = transform(source, filename);
  module._compile(result.code, filename);
};

module.exports = { scope };