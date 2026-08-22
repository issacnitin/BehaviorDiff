'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { AsyncLocalStorage } = require('node:async_hooks');
const { types } = require('node:util');
const { canonicalize } = require('./canonicalize.cjs');

const RUNTIME_SYMBOL = Symbol.for('behaviordiff.runtime');
const SCHEMA = 'behaviordiff.trace/1';
const WRITER_CAPACITY = 100000;
const DEFAULT_REDACT_NAMES = 'password;token;secret;key;ssn;email;auth;credential';

function configuredList(name, fallback = '') {
  return (process.env[name] ?? fallback).split(/[;,]/).map(value => value.trim()).filter(Boolean);
}

function normalizedPath(value) {
  return value.replaceAll('\\', '/').replace(/^\/+|\/+$/g, '');
}

function decorate(configuredPath, processId, suffix) {
  const fullPath = path.resolve(configuredPath);
  const extension = path.extname(fullPath);
  const name = path.basename(fullPath, extension);
  return path.join(path.dirname(fullPath), `${name}.${processId}${suffix}${extension}`);
}

function addCounters(target, source) {
  for (const name of Object.keys(target)) {
    target[name] += source[name];
  }
}

function writeExact(descriptor, text) {
  const buffer = Buffer.from(text, 'utf8');
  let offset = 0;
  while (offset < buffer.length) {
    offset += fs.writeSync(descriptor, buffer, offset, buffer.length - offset);
  }
}

function exceptionType(error) {
  if (!types.isNativeError(error)) {
    return 'ThrownValue';
  }
  try {
    const prototype = Object.getPrototypeOf(error);
    const constructor = Object.getOwnPropertyDescriptor(prototype, 'constructor')?.value;
    const name = constructor && Object.getOwnPropertyDescriptor(constructor, 'name')?.value;
    return typeof name === 'string' && name ? name : 'Error';
  } catch {
    return 'Error';
  }
}

function createRuntime(options = {}) {
  const configuredPath = options.tracePath ?? process.env.BEHAVIORDIFF_TRACE;
  const processId = options.processId ?? process.pid;
  const storage = new AsyncLocalStorage();
  const redaction = {
    sensitiveNames: configuredList('BEHAVIORDIFF_REDACT_NAMES', DEFAULT_REDACT_NAMES),
    digestOnlyTypes: configuredList('BEHAVIORDIFF_REDACT_TYPES'),
    digestOnlyPaths: configuredList('BEHAVIORDIFF_REDACT_PATHS').map(normalizedPath),
  };
  const modules = new Map();
  const ordinals = new Map();
  const digestCounters = {
    valuesDigested: 0,
    depthLimited: 0,
    blocklisted: 0,
    errored: 0,
    renderedTruncated: 0
  };
  let nextCallId = 1;
  let eventCount = 0;
  let closed = false;
  let tracePath;
  let manifestPath;
  let traceDescriptor;

  if (configuredPath) {
    tracePath = decorate(configuredPath, processId, '');
    manifestPath = decorate(configuredPath, processId, '.manifest');
    fs.mkdirSync(path.dirname(tracePath), { recursive: true });
    traceDescriptor = fs.openSync(tracePath, 'w');
  }

  function capture(value, metadata, parameterNames) {
    try {
      const result = canonicalize(value);
      const sourcePath = normalizedPath(metadata?.filePath ?? '');
      const redactPath = sourcePath !== '' && redaction.digestOnlyPaths.some(prefix =>
        sourcePath === prefix || sourcePath.startsWith(`${prefix}/`)
          || sourcePath.endsWith(`/${prefix}`) || sourcePath.includes(`/${prefix}/`));
      const rendered = redactPath
        ? '<redacted>'
        : canonicalize(value, { ...redaction, parameterNames, redact: true }).rendered;
      addCounters(digestCounters, result.counters);
      return { digest: result.digest, rendered };
    } catch {
      digestCounters.errored++;
      return null;
    }
  }

  function enter(metadata, args, contextOverride) {
    const parent = storage.getStore();
    const testId = contextOverride?.testId ?? parent?.testId ?? '(no-test)';
    const ordinalKey = `${testId}\u0000${metadata.methodFullName}`;
    const ordinal = ordinals.get(ordinalKey) ?? 0;
    ordinals.set(ordinalKey, ordinal + 1);
    return {
      metadata,
      testId,
      callId: nextCallId++,
      parentCallId: parent?.callId,
      callDepth: parent ? parent.callDepth + 1 : 0,
      ordinal,
      args: capture(args, metadata, metadata.parameterNames)
    };
  }

  function emit(frame, result, error, includeReturn) {
    if (traceDescriptor === undefined || closed) {
      return;
    }
    const metadata = frame.metadata;
    const event = {
      testId: frame.testId,
      methodFullName: metadata.methodFullName,
      filePathResolution: metadata.filePathResolution ?? 'debugInfo',
      line: metadata.line,
      callDepth: frame.callDepth,
      callId: frame.callId,
      ordinal: frame.ordinal,
      threadId: 0
    };
    if (metadata.filePath !== undefined) event.filePath = metadata.filePath;
    if (frame.parentCallId !== undefined) event.parentCallId = frame.parentCallId;
    if (frame.args) {
      event.argsDigest = frame.args.digest;
      event.argsRendered = frame.args.rendered;
    }
    if (error !== undefined) {
      event.exceptionType = exceptionType(error);
    } else if (includeReturn) {
      const captured = capture(result, metadata);
      if (captured) {
        event.returnDigest = captured.digest;
        event.returnRendered = captured.rendered;
      }
    }
    if (metadata.isHarness === true) event.isHarness = true;
    writeExact(traceDescriptor, `${JSON.stringify(event)}\n`);
    eventCount++;
    const module = modules.get(metadata.assembly ?? metadata.filePath);
    if (module) module.tracedCalls++;
  }

  function runSync(metadata, args, callback) {
    const frame = enter(metadata, args);
    return storage.run(frame, () => {
      try {
        const result = callback();
        emit(frame, result, undefined, true);
        return result;
      } catch (error) {
        emit(frame, undefined, error, false);
        throw error;
      }
    });
  }

  function runAsync(metadata, args, callback) {
    const frame = enter(metadata, args);
    return storage.run(frame, async () => {
      try {
        const result = await callback();
        emit(frame, result, undefined, true);
        return result;
      } catch (error) {
        emit(frame, undefined, error, false);
        throw error;
      }
    });
  }

  function registerModule(module) {
    const members = module.members.map(member => ({ ...member, assembly: module.assembly }));
    modules.set(module.assembly, {
      assembly: module.assembly,
      members,
      tracedCalls: 0
    });
  }

  function withTestRoot(testId, callback) {
    const metadata = {
      assembly: '(node-harness)',
      methodFullName: '(node-harness)#withTestRoot',
      filePathResolution: 'unresolved',
      line: 0,
      isHarness: true
    };
    if (!modules.has(metadata.assembly)) {
      registerModule({
        assembly: metadata.assembly,
        members: [{
          methodFullName: metadata.methodFullName,
          status: 'Patched',
          returnKind: 'Dynamic',
          isTestRoot: true,
          sourceResolution: 'unresolved'
        }]
      });
    }
    const frame = enter(metadata, [], { testId });
    return storage.run(frame, () => {
      let result;
      try {
        result = callback();
      } catch (error) {
        emit(frame, undefined, error, false);
        throw error;
      }
      if (result && typeof result.then === 'function') {
        return Promise.resolve(result).then(
          value => { emit(frame, value, undefined, true); return value; },
          error => { emit(frame, undefined, error, false); throw error; }
        );
      }
      emit(frame, result, undefined, true);
      return result;
    });
  }

  function shutdown() {
    if (closed) return;
    closed = true;
    if (traceDescriptor === undefined) return;
    fs.closeSync(traceDescriptor);
    const records = [{ kind: 'run', schema: SCHEMA, language: 'node' }];
    for (const module of modules.values()) {
      const patchedMembers = module.members.filter(member => member.status === 'Patched').length;
      const skippedMembers = module.members.length - patchedMembers;
      records.push({
        kind: 'assembly',
        assembly: module.assembly,
        discovery: 'NodeAstTransform',
        scanned: true,
        instrumented: patchedMembers > 0,
        patchedMembers,
        discoveredMembers: module.members.length,
        skippedMembers,
        patchFailedMembers: 0,
        queuedAtMs: 0,
        patchedAtMs: 0,
        tracedCalls: module.tracedCalls,
        membersWithExactSource: module.members.filter(member =>
          member.status === 'Patched' && member.sourceResolution === 'debugInfo').length,
        exactSourcePercent: patchedMembers === 0 ? 100 : Math.floor(
          module.members.filter(member => member.status === 'Patched' && member.sourceResolution === 'debugInfo').length
            * 100 / patchedMembers),
        sourceRule: patchedMembers === 0 ? 'notApplicable' : 'ratio'
      });
      for (const member of module.members) {
        const record = {
          kind: 'member',
          assembly: module.assembly,
          method: member.methodFullName,
          status: member.status,
          returnKind: member.returnKind,
          sourceResolution: member.sourceResolution
        };
        if (member.skipReason) record.skipReason = member.skipReason;
        if (member.detail) record.detail = member.detail;
        if (member.isTestRoot === true) record.isTestRoot = true;
        records.push(record);
      }
    }
    records.push({ kind: 'digest', ...digestCounters });
    records.push({
      kind: 'writer',
      enqueued: eventCount,
      written: eventCount,
      dropped: 0,
      capacity: WRITER_CAPACITY
    });
    fs.writeFileSync(manifestPath, `${records.map(record => JSON.stringify(record)).join('\n')}\n`, 'utf8');
  }

  return {
    runSync,
    runAsync,
    withTestRoot,
    registerModule,
    shutdown,
    tracePath,
    manifestPath
  };
}

function initializeRuntime(options) {
  if (!globalThis[RUNTIME_SYMBOL]) {
    const runtime = createRuntime(options);
    globalThis[RUNTIME_SYMBOL] = runtime;
    process.once('exit', () => runtime.shutdown());
  }
  return globalThis[RUNTIME_SYMBOL];
}

module.exports = { createRuntime, initializeRuntime, RUNTIME_SYMBOL };