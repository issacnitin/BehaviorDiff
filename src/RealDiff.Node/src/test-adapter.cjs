'use strict';

const { canonicalize } = require('./canonicalize.cjs');
const { initializeRuntime } = require('./runtime.cjs');

const FORWARDED_METHODS = ['only', 'skip', 'todo'];

function testName(value) {
  try {
    return String(value);
  } catch {
    return '<unnamed>';
  }
}

function createTestAdapter(testFunction, framework, options = {}) {
  if (typeof testFunction !== 'function') {
    throw new TypeError('RealDiff test adapter requires a test function.');
  }
  if (typeof framework !== 'string' || framework.length === 0) {
    throw new TypeError('RealDiff test adapter requires a framework name.');
  }

  const runtime = options.runtime ?? initializeRuntime();
  const wrappers = new WeakMap();

  function caseId(name, callbackArgs) {
    return `${framework}:${testName(name)}#case=${canonicalize(callbackArgs).digest}`;
  }

  function wrapRegistration(registration, instrument, tableCase) {
    if (typeof registration !== 'function') return registration;

    let variants = wrappers.get(registration);
    if (!variants) {
      variants = new Map();
      wrappers.set(registration, variants);
    }
    const key = `${instrument}:${tableCase}`;
    if (variants.has(key)) return variants.get(key);

    function wrappedTest(...registrationArgs) {
      const callbackIndex = registrationArgs.findIndex((value, index) =>
        index > 0 && typeof value === 'function');
      if (!instrument || callbackIndex === -1) {
        return Reflect.apply(registration, this, registrationArgs);
      }

      const name = registrationArgs[0];
      const callback = registrationArgs[callbackIndex];
      const wrappedCallback = function behaviorDiffTestCallback(...callbackArgs) {
        const id = tableCase
          ? caseId(name, callbackArgs)
          : `${framework}:${testName(name)}`;
        return runtime.withTestRoot(id, () => Reflect.apply(callback, this, callbackArgs));
      };
      const forwardedArgs = [...registrationArgs];
      forwardedArgs[callbackIndex] = wrappedCallback;
      return Reflect.apply(registration, this, forwardedArgs);
    }

    variants.set(key, wrappedTest);

    for (const method of FORWARDED_METHODS) {
      const member = registration[method];
      if (typeof member !== 'function') continue;
      const memberInstrument = instrument && method === 'only';
      Object.defineProperty(wrappedTest, method, {
        configurable: true,
        enumerable: true,
        value: wrapRegistration(member, memberInstrument, tableCase)
      });
    }

    if (typeof registration.each === 'function') {
      Object.defineProperty(wrappedTest, 'each', {
        configurable: true,
        enumerable: true,
        value: function behaviorDiffEach(...tableArgs) {
          const generatedRegistration = Reflect.apply(registration.each, registration, tableArgs);
          return wrapRegistration(generatedRegistration, instrument, true);
        }
      });
    }

    return wrappedTest;
  }

  return wrapRegistration(testFunction, true, false);
}

module.exports = { createTestAdapter };