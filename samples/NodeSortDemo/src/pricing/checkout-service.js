'use strict';

class CheckoutService {
  headline() {
    const { CheckoutTotals } = require('./checkout-totals.js');
    return new CheckoutTotals();
  }
}

module.exports = { CheckoutService };