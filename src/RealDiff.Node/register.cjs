'use strict';

const fs = require('node:fs');
const Module = require('node:module');
const { createScope } = require('./src/scope.cjs');
const { createSourceResolver } = require('./src/source-map.cjs');
const { transform } = require('./src/transform.cjs');
const { initializeRuntime } = require('./src/runtime.cjs');

const scope = createScope();
const runtime = initializeRuntime();
const original = Module._extensions['.js'];
Module._extensions['.js'] = function behaviorDiffExtension(module, filename) {
  if (!scope.isIncluded(filename)) {
    return original(module, filename);
  }
  const source = fs.readFileSync(filename, 'utf8');
  const sourceResolver = createSourceResolver(source, filename);
  if (scope.isExcluded(filename)) {
    const result = transform(source, filename, {
      sourceResolver,
      instrument: false,
      skipReason: 'ExcludedByScope',
      skipDetail: 'Node: ExcludedByScope'
    });
    for (const moduleRegistration of result.modules) runtime.registerModule(moduleRegistration);
    return original(module, filename);
  }
  const result = transform(source, filename, { sourceResolver });
  module._compile(result.code, filename);
};

module.exports = { scope, runtime };