'use strict';

class Rule {
  constructor(code, priority, minimumTotal) {
    this.code = code;
    this.priority = priority;
    this.minimumTotal = minimumTotal;
  }
}

module.exports = { Rule };