'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { CheckoutService } = require('../src/pricing/checkout-service.js');

const runtimeSymbol = Symbol.for('realdiff.runtime');
let executed = 0;

async function runCase(testId, callback) {
  const runtime = globalThis[runtimeSymbol];
  const result = runtime && typeof runtime.withTestRoot === 'function'
    ? runtime.withTestRoot(testId, callback)
    : callback();
  await result;
  executed++;
}

async function main() {
  await runCase('node-sort-demo/discount-is-applied', function discountIsApplied() {
    const checkout = new CheckoutService().headline();
    const total = checkout.compute(100);
    assert.ok(total < 100);
  });

  await runCase('node-sort-demo/total-never-above-list-price', function totalNeverAboveListPrice() {
    const checkout = new CheckoutService().headline();
    const total = checkout.compute(100);
    assert.ok(total <= 100);
  });

  await runCase('node-sort-demo/clearance-wins-current-ties', function clearanceWinsCurrentTies() {
    const checkout = new CheckoutService().headline();
    checkout.compute(100);
    assert.equal(checkout.selectedCode, 'Z_CLEARANCE');
  });

  assert.equal(executed, 3);
  const report = { runnerTests: executed };
  const serialized = `${JSON.stringify(report)}\n`;
  if (process.env.REALDIFF_RUNNER_REPORT) {
    const reportPath = path.resolve(process.env.REALDIFF_RUNNER_REPORT);
    fs.mkdirSync(path.dirname(reportPath), { recursive: true });
    fs.writeFileSync(reportPath, serialized, 'utf8');
  }
  process.stdout.write(serialized);
}

main().catch(function reportFailure(error) {
  process.stderr.write(`${error.stack || error}\n`);
  process.exitCode = 1;
});