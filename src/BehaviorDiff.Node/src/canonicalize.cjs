'use strict';

const { createHash } = require('node:crypto');
const { types } = require('node:util');

const DEFAULT_RENDERED_CAP = 2000;
const DEFAULT_MAX_DEPTH = 6;
const DEFAULT_MAX_PROTOTYPE_DEPTH = 2;
const DEFAULT_MAX_ENTRIES = 100;
const ARRAY_INDEX_MAX = 0xfffffffe;
const DEFAULT_SENSITIVE_NAMES = ['password', 'token', 'secret', 'key', 'ssn', 'email', 'auth', 'credential'];
const CREDENTIAL_PATTERNS = [
  /\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b/,
  /\b(?:AKIA|ASIA)[0-9A-Z]{16}\b/,
  /-----BEGIN [A-Z0-9 ]*(?:PRIVATE KEY|CERTIFICATE)-----/,
  /(?:^|[^A-Za-z0-9+/])(?:[A-Za-z0-9+/]{40,}={0,2})(?:$|[^A-Za-z0-9+/=])/,
];

const intrinsicPrototypes = new Map([
  [Object.prototype, 'Object.prototype'],
  [Array.prototype, 'Array.prototype'],
  [Function.prototype, 'Function.prototype'],
  [Map.prototype, 'Map.prototype'],
  [Set.prototype, 'Set.prototype']
]);

function nonNegativeInteger(value, fallback, name) {
  if (value === undefined) {
    return fallback;
  }
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new RangeError(`${name} must be a non-negative safe integer`);
  }
  return value;
}

function compareStrings(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

function isArrayIndex(key) {
  if (typeof key !== 'string' || key === '') {
    return false;
  }
  const index = Number(key);
  return Number.isInteger(index) && index >= 0 && index <= ARRAY_INDEX_MAX && String(index) === key;
}

function createWriter(renderedCap) {
  const hash = createHash('sha256');
  const rendered = [];
  let renderedLength = 0;
  let wasTruncated = false;

  return {
    write(text) {
      hash.update(text, 'utf8');
      if (renderedLength >= renderedCap) {
        wasTruncated = true;
        return;
      }

      const remaining = renderedCap - renderedLength;
      if (text.length <= remaining) {
        rendered.push(text);
        renderedLength += text.length;
        return;
      }

      let end = remaining;
      if (end > 0 && /[\uD800-\uDBFF]/.test(text[end - 1]) && /[\uDC00-\uDFFF]/.test(text[end])) {
        end--;
      }
      rendered.push(text.slice(0, end));
      renderedLength += end;
      wasTruncated = true;
    },
    finish() {
      return {
        digest: `sha256:${hash.digest('hex')}`,
        rendered: rendered.join('') + (wasTruncated ? '<truncated>' : ''),
        wasTruncated
      };
    }
  };
}

function canonicalize(value, options = {}) {
  const renderedCap = nonNegativeInteger(options.renderedCap, DEFAULT_RENDERED_CAP, 'renderedCap');
  const maxDepth = nonNegativeInteger(options.maxDepth, DEFAULT_MAX_DEPTH, 'maxDepth');
  const maxPrototypeDepth = nonNegativeInteger(
    options.maxPrototypeDepth,
    DEFAULT_MAX_PROTOTYPE_DEPTH,
    'maxPrototypeDepth'
  );
  const maxEntries = nonNegativeInteger(options.maxEntries, DEFAULT_MAX_ENTRIES, 'maxEntries');
  const redact = options.redact === true;
  const sensitiveNames = DEFAULT_SENSITIVE_NAMES.concat(options.sensitiveNames ?? [])
    .map(name => String(name).toLowerCase());
  const digestOnlyTypes = (options.digestOnlyTypes ?? []).map(name => String(name).toLowerCase());
  const parameterNames = options.parameterNames ?? [];
  const writer = createWriter(renderedCap);
  const counters = {
    valuesDigested: 0,
    depthLimited: 0,
    blocklisted: 0,
    errored: 0,
    renderedTruncated: 0
  };
  const references = new Map();
  const symbols = new Map();
  let partial = false;

  function marker(text, counter) {
    partial = true;
    counters[counter]++;
    writer.write(text);
  }

  function symbolLabel(symbol) {
    const globalKey = Symbol.keyFor(symbol);
    if (globalKey !== undefined) {
      return `global:${JSON.stringify(globalKey)}`;
    }
    if (!symbols.has(symbol)) {
      symbols.set(symbol, symbols.size);
    }
    return `local:${symbols.get(symbol)}`;
  }

  function keyLabel(key) {
    return typeof key === 'string'
      ? `string:${JSON.stringify(key)}`
      : `symbol:${symbolLabel(key)}`;
  }

  function writeNumber(number) {
    if (Number.isNaN(number)) {
      writer.write('number:NaN');
    } else if (number === Infinity) {
      writer.write('number:+Infinity');
    } else if (number === -Infinity) {
      writer.write('number:-Infinity');
    } else if (Object.is(number, -0)) {
      writer.write('number:-0');
    } else {
      writer.write(`number:${String(number)}`);
    }
  }

  function writeScalar(current) {
    if (current === null) {
      writer.write('null');
      return true;
    }

    switch (typeof current) {
      case 'undefined':
        writer.write('undefined');
        return true;
      case 'boolean':
        writer.write(current ? 'boolean:true' : 'boolean:false');
        return true;
      case 'number':
        writeNumber(current);
        return true;
      case 'bigint':
        writer.write(`bigint:${String(current)}n`);
        return true;
      case 'string':
        writer.write(`string:${JSON.stringify(current)}`);
        return true;
      case 'symbol':
        writer.write(`symbol:${symbolLabel(current)}`);
        return true;
      default:
        return false;
    }
  }

  function readShape(current) {
    try {
      const keys = Reflect.ownKeys(current);
      const descriptors = Object.getOwnPropertyDescriptors(current);
      return { keys, descriptors };
    } catch {
      return null;
    }
  }

  function writeDescriptor(key, descriptor, depth, prototypeDepth) {
    writer.write(keyLabel(key));
    writer.write('=');
    if (!descriptor || !Object.prototype.hasOwnProperty.call(descriptor, 'value')) {
      marker('<skipped:accessor>', 'blocklisted');
      return;
    }

    const effectiveName = depth === 1 && isArrayIndex(key)
      ? parameterNames[Number(key)]
      : typeof key === 'string' ? key : undefined;
    if (redact && effectiveName !== undefined
        && sensitiveNames.some(pattern => effectiveName.toLowerCase().includes(pattern))) {
      writer.write('<redacted>');
      return;
    }

    writer.write(`[e${descriptor.enumerable ? 1 : 0}c${descriptor.configurable ? 1 : 0}w${descriptor.writable ? 1 : 0}]`);
    writeValue(descriptor.value, depth, prototypeDepth);
  }

  function writeDescriptorList(keys, descriptors, depth, prototypeDepth) {
    const stringKeys = [];
    const symbolKeys = [];
    for (const key of keys) {
      if (typeof key === 'string') {
        stringKeys.push(key);
      } else {
        symbolLabel(key);
        symbolKeys.push(key);
      }
    }
    stringKeys.sort(compareStrings);
    symbolKeys.sort((left, right) => compareStrings(symbolLabel(left), symbolLabel(right)));

    const sortedKeys = stringKeys.concat(symbolKeys);
    const count = Math.min(sortedKeys.length, maxEntries);
    for (let index = 0; index < count; index++) {
      if (index > 0) {
        writer.write(',');
      }
      const key = sortedKeys[index];
      writeDescriptor(key, descriptors[key], depth, prototypeDepth);
    }
    if (sortedKeys.length > count) {
      if (count > 0) {
        writer.write(',');
      }
      marker(`<skipped:entries:${sortedKeys.length - count}>`, 'blocklisted');
    }
  }

  function writePrototype(current, depth, prototypeDepth) {
    writer.write(';prototype=');
    let prototype;
    try {
      prototype = Object.getPrototypeOf(current);
    } catch {
      marker('<error:prototype>', 'errored');
      return;
    }

    if (prototype === null) {
      writer.write('null');
      return;
    }
    const intrinsicName = intrinsicPrototypes.get(prototype);
    if (intrinsicName !== undefined) {
      writer.write(`intrinsic:${intrinsicName}`);
      return;
    }
    if (prototypeDepth >= maxPrototypeDepth) {
      marker('<depth:prototype>', 'depthLimited');
      return;
    }
    writeValue(prototype, depth + 1, prototypeDepth + 1);
  }

  function writeArray(current, reference, depth, prototypeDepth) {
    const shape = readShape(current);
    if (shape === null) {
      marker('<error:descriptors>', 'errored');
      return;
    }

    const lengthDescriptor = shape.descriptors.length;
    const length = lengthDescriptor && Object.prototype.hasOwnProperty.call(lengthDescriptor, 'value')
      ? lengthDescriptor.value
      : '?';
    writer.write(`array#${reference}(length=${String(length)};indices={`);
    const indexKeys = shape.keys.filter(isArrayIndex).sort((left, right) => Number(left) - Number(right));
    writeDescriptorList(indexKeys, shape.descriptors, depth + 1, prototypeDepth);
    writer.write('};properties={');
    const otherKeys = shape.keys.filter(key => key !== 'length' && !isArrayIndex(key));
    writeDescriptorList(otherKeys, shape.descriptors, depth + 1, prototypeDepth);
    writer.write('}');
    writePrototype(current, depth, prototypeDepth);
    writer.write(')');
  }

  function writeObject(current, reference, depth, prototypeDepth) {
    const shape = readShape(current);
    if (shape === null) {
      marker('<error:descriptors>', 'errored');
      return;
    }

    writer.write(`object#${reference}({`);
    writeDescriptorList(shape.keys, shape.descriptors, depth + 1, prototypeDepth);
    writer.write('}');
    writePrototype(current, depth, prototypeDepth);
    writer.write(')');
  }

  function writeValue(current, depth, prototypeDepth) {
    counters.valuesDigested++;
    if (redact && typeof current === 'string'
        && CREDENTIAL_PATTERNS.some(pattern => pattern.test(current))) {
      writer.write('<redacted>');
      return;
    }
    if (current !== null && (typeof current === 'object' || typeof current === 'function') && types.isProxy(current)) {
      marker('<skipped:Proxy>', 'blocklisted');
      return;
    }
    if (redact && current !== null && (typeof current === 'object' || typeof current === 'function')) {
      let constructorName = '';
      try {
        constructorName = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(current), 'constructor')?.value?.name ?? '';
      } catch {
        constructorName = '';
      }
      if (digestOnlyTypes.some(pattern => constructorName.toLowerCase() === pattern
          || constructorName.toLowerCase().startsWith(`${pattern}.`))) {
        writer.write('<redacted>');
        return;
      }
    }
    if (writeScalar(current)) {
      return;
    }

    const existingReference = references.get(current);
    if (existingReference !== undefined) {
      writer.write(`<ref:${existingReference}>`);
      return;
    }
    const reference = references.size;
    references.set(current, reference);

    if (depth >= maxDepth) {
      marker(`<depth:${Array.isArray(current) ? 'Array' : 'Object'}>`, 'depthLimited');
      return;
    }
    if (types.isMap(current)) {
      writer.write(`map#${reference}:`);
      marker('<skipped:Map>', 'blocklisted');
      return;
    }
    if (types.isSet(current)) {
      writer.write(`set#${reference}:`);
      marker('<skipped:Set>', 'blocklisted');
      return;
    }
    if (typeof current === 'function') {
      writer.write(`function#${reference}:`);
      marker('<skipped:Function>', 'blocklisted');
      return;
    }
    if (Array.isArray(current)) {
      writeArray(current, reference, depth, prototypeDepth);
      return;
    }
    writeObject(current, reference, depth, prototypeDepth);
  }

  writeValue(value, 0, 0);
  const result = writer.finish();
  if (result.wasTruncated) {
    partial = true;
    counters.renderedTruncated++;
  }
  return {
    digest: result.digest,
    rendered: result.rendered,
    partial,
    counters
  };
}

module.exports = { canonicalize };