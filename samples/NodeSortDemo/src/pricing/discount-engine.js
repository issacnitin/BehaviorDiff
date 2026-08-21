'use strict';

const { Rule } = require('./rule.js');
const { byPriority } = require('../sorting/rule-ordering.js');

class DiscountEngine {
  constructor() {
    this.rules = [
      new Rule('Z_CLEARANCE', 10, 50),
      new Rule('A_SEASONAL', 10, 50),
      new Rule('INELIGIBLE', 10, 1000)
    ];
  }

  selectDiscount(listPrice) {
    for (const rule of byPriority(this.rules)) {
      if (listPrice >= rule.minimumTotal) {
        return rule.code;
      }
    }
    throw new Error('No eligible discount rule');
  }
}

module.exports = { DiscountEngine };