'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { fileURLToPath, pathToFileURL } = require('node:url');
const { TraceMap, originalPositionFor } = require('@jridgewell/trace-mapping');

const SOURCE_MAP_PATTERN = /^\s*\/\/[#@]\s*sourceMappingURL\s*=\s*(\S+)\s*$/gm;

function relativeSourcePath(filename, repositoryRoot = process.cwd()) {
  return path.relative(path.resolve(repositoryRoot), path.resolve(filename)).replaceAll('\\', '/');
}

function sourceMappingUrl(source) {
  let value = null;
  for (const match of source.matchAll(SOURCE_MAP_PATTERN)) value = match[1];
  return value;
}

function inlineMap(value) {
  const match = /^data:application\/json(?:;charset=[^;,]+)?(;base64)?,(.*)$/i.exec(value);
  if (!match) throw new Error('Unsupported inline source map');
  const json = match[1]
    ? Buffer.from(match[2], 'base64').toString('utf8')
    : decodeURIComponent(match[2]);
  return JSON.parse(json);
}

function mappedFilePath(source, repositoryRoot) {
  if (!source) return undefined;
  let filename;
  try {
    const url = new URL(source);
    if (url.protocol !== 'file:') return undefined;
    filename = fileURLToPath(url);
  } catch {
    filename = source;
  }
  return relativeSourcePath(filename, repositoryRoot);
}

function unresolvedResolver() {
  return {
    mapState: 'unresolved',
    resolve() {
      return { resolution: 'unresolved', line: 0, column: 0 };
    }
  };
}

function createSourceResolver(source, filename, options = {}) {
  const repositoryRoot = options.repositoryRoot ?? process.cwd();
  const generatedPath = relativeSourcePath(filename, repositoryRoot);
  const mappingUrl = sourceMappingUrl(source);
  if (mappingUrl === null) {
    return {
      mapState: 'direct',
      resolve(line, column) {
        return {
          resolution: 'debugInfo',
          filePath: generatedPath,
          line: line ?? 0,
          column: column ?? 0
        };
      }
    };
  }

  try {
    let map;
    let mapUrl;
    if (mappingUrl.startsWith('data:')) {
      map = inlineMap(mappingUrl);
      mapUrl = pathToFileURL(filename).href;
    } else {
      const mapFilename = path.resolve(path.dirname(filename), decodeURIComponent(mappingUrl));
      map = JSON.parse(fs.readFileSync(mapFilename, 'utf8'));
      mapUrl = pathToFileURL(mapFilename).href;
    }
    const traceMap = new TraceMap(map, mapUrl);
    return {
      mapState: 'mapped',
      resolve(line, column) {
        if (!line || column === undefined) {
          return { resolution: 'unresolved', line: 0, column: 0 };
        }
        const original = originalPositionFor(traceMap, { line, column });
        const filePath = mappedFilePath(original.source, repositoryRoot);
        if (!filePath || original.line === null || original.column === null) {
          return { resolution: 'unresolved', line: 0, column: 0 };
        }
        return {
          resolution: 'debugInfo',
          filePath,
          line: original.line,
          column: original.column
        };
      }
    };
  } catch {
    return unresolvedResolver();
  }
}

module.exports = { createSourceResolver };