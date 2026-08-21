'use strict';

function leaf(value) {
  return value * 2;
}

function work(value) {
  return leaf(value) + 1;
}

async function asyncWork(value) {
  await Promise.resolve();
  return work(value);
}

module.exports = { asyncWork, leaf, work };