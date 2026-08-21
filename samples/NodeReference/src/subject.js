'use strict';

const observed = [];

function observe(value) {
  return value * 2 + 1;
}

function noteObservedCall(name) {
  observed.push(name);
}

function resetObservedCalls() {
  observed.length = 0;
}

function observedCalls() {
  return observed.join(',');
}

function inspect(value) {
  return value === null ? 0 : 1;
}

function cycle(value) {
  return value === null ? 0 : 1;
}

function topology(value) {
  return value === null ? 0 : 1;
}

function map(reverse) {
  const value = new Map();
  value.set(reverse ? 'b' : 'a', reverse ? 2 : 1);
  value.set(reverse ? 'a' : 'b', reverse ? 1 : 2);
  return value;
}

function set(reverse) {
  const value = new Set();
  value.add(reverse ? 'b' : 'a');
  value.add(reverse ? 'a' : 'b');
  return value;
}

function stamp(value) {
  return value === null ? 0 : 1;
}

function block(value) {
  return value === null ? 0 : 1;
}

function deep(value) {
  return value === null ? 0 : 1;
}

function longText(value) {
  return value.length;
}

function unreadable(value) {
  return value === null ? 0 : 1;
}

function scalar(value) {
  return value;
}

function throwsNow() {
  throw new Error('reference throw');
}

async function future(value) {
  await Promise.resolve();
  return value;
}

class BaseFormatter {
  constructor(prefix) {
    this.prefix = prefix;
  }

  describe(value) {
    return `${this.prefix}:${value}`;
  }

  decorate(value) {
    return `[${this.describe(value)}]`;
  }
}

class DerivedFormatter extends BaseFormatter {
  describe(value) {
    return super.describe(value).toUpperCase();
  }
}

function dispatchFormatter(formatter, value) {
  return formatter.decorate(value);
}

class ValueBox {
  constructor(value) {
    this.value = value;
  }

  current() {
    return this.value;
  }

  scale(factor) {
    return this.value * factor;
  }

  static create(value) {
    return new ValueBox(value);
  }
}

function dispatchValueBox(value) {
  const box = ValueBox.create(value);
  return { current: box.current(), scaled: box.scale(3) };
}

const arrowIncrement = value => value + 1;
const arrowMultiply = (value, factor) => value * factor;
const arrowLabel = (prefix, value) => `${prefix}:${value}`;

function dispatchArrows(value) {
  return arrowLabel('arrow', arrowMultiply(arrowIncrement(value), 3));
}

const objectPipeline = {
  normalize(value) {
    return value.trim().toLowerCase();
  },
  decorate(value) {
    return `<${this.normalize(value)}>`;
  },
  summarize(value) {
    return { text: this.decorate(value), length: this.normalize(value).length };
  }
};

function dispatchObjectPipeline(value) {
  return objectPipeline.summarize(value);
}

function createClosurePipeline(offset, multiplier) {
  function addOffset(value) {
    return value + offset;
  }

  function multiply(value) {
    return value * multiplier;
  }

  function apply(value) {
    return multiply(addOffset(value));
  }

  return apply;
}

function dispatchClosurePipeline(value, offset, multiplier) {
  return createClosurePipeline(offset, multiplier)(value);
}

function requireNonNegative(value) {
  if (value < 0) {
    throw new RangeError(`negative reading: ${value}`);
  }
  return value;
}

function isEven(value) {
  return value % 2 === 0;
}

function doubleReading(value) {
  return value * 2;
}

function processReadings(values) {
  const accepted = [];
  let recovered = 0;
  for (const value of values) {
    try {
      accepted.push(requireNonNegative(value));
    } catch (error) {
      if (!(error instanceof RangeError)) {
        throw error;
      }
      recovered++;
    }
  }
  return { values: accepted.filter(isEven).map(doubleReading), recovered };
}

class AsyncSettlement {
  constructor(suffix) {
    this.suffix = suffix;
  }

  async settle(value) {
    await Promise.resolve();
    return `${value}:${this.suffix}`;
  }
}

function incrementPromise(value) {
  return value + 1;
}

function labelPromise(value) {
  return `settled=${value}`;
}

function promiseWorkflow(value) {
  const settlement = new AsyncSettlement('done');
  function settleValue(next) {
    return settlement.settle(next);
  }
  return Promise.resolve(value)
    .then(incrementPromise)
    .then(settleValue)
    .then(labelPromise);
}

function* unsupportedGenerator() {
  yield 'unsupported';
}

const unsupportedDestructuredArrow = ({ value }) => value;

module.exports = {
  observe,
  noteObservedCall,
  resetObservedCalls,
  observedCalls,
  inspect,
  cycle,
  topology,
  map,
  set,
  stamp,
  block,
  deep,
  longText,
  unreadable,
  scalar,
  throwsNow,
  future,
  BaseFormatter,
  DerivedFormatter,
  dispatchFormatter,
  ValueBox,
  dispatchValueBox,
  dispatchArrows,
  dispatchObjectPipeline,
  dispatchClosurePipeline,
  processReadings,
  promiseWorkflow,
  unsupportedGenerator,
  unsupportedDestructuredArrow
};