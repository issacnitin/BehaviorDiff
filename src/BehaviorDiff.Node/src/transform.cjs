'use strict';

function transform(source, filename) {
  return { code: source, filename };
}

module.exports = { transform };