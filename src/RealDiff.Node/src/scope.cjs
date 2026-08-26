'use strict';

const path = require('node:path');

function readList(value) {
  return (value || '')
    .split(/[;,]/)
    .map(item => item.trim().replaceAll('\\', '/').replace(/\/$/, ''))
    .filter((item, index, values) => item && values.indexOf(item) === index);
}

function segmentMatch(value, prefix) {
  return value === prefix || value.startsWith(`${prefix}/`);
}

function createScope(options = {}, environment = process.env) {
  const includes = readList(options.include ?? environment.REALDIFF_NAMESPACES);
  const excludes = readList(options.exclude ?? environment.REALDIFF_EXCLUDE_NAMESPACES);
  if (includes.length === 0) {
    throw new Error('RealDiff Node requires an include scope');
  }

  function normalize(filename) {
    const absolute = path.resolve(filename).replaceAll('\\', '/');
    const relative = path.relative(process.cwd(), absolute).replaceAll('\\', '/');
    return relative.startsWith('../') ? absolute : relative;
  }

  return {
    includes,
    excludes,
    normalize,
    isIncluded(filename) {
      const value = normalize(filename);
      return includes.some(prefix => segmentMatch(value, prefix));
    },
    isExcluded(filename) {
      const value = normalize(filename);
      return excludes.some(prefix => segmentMatch(value, prefix));
    },
    selects(filename) {
      return this.isIncluded(filename) && !this.isExcluded(filename);
    }
  };
}

module.exports = { createScope, readList, segmentMatch };