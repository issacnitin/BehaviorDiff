'use strict';

function byPriority(rules) {
  return [...rules].sort((a, b) => a.priority - b.priority);
}

module.exports = { byPriority };