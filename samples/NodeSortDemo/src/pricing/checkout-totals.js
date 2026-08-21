'use strict';

const { DecisionPipeline } = require('../pipeline/decision-pipeline.js');
const { DiscountEngine } = require('./discount-engine.js');

class CheckoutTotals {
  constructor() {
    this.discounts = new DiscountEngine();
    this.pipeline = new DecisionPipeline();
    this.selectedCode = undefined;
  }

  compute(listPrice) {
    this.selectedCode = this.pipeline.execute(listPrice, this.discounts);
    return this.selectedCode === 'A_SEASONAL' ? listPrice * 0.85 : listPrice * 0.60;
  }
}

module.exports = { CheckoutTotals };