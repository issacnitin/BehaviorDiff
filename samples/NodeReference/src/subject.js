'use strict';

const observed = [];

function observe(value) {
  return value * 2 + 1;
}

function noteObservedCall(name) {
  observed.push(name);
}

function resetObservedCalls() {
  observed.length = 0;
}

function observedCalls() {
  return observed.join(',');
}

function inspect(value) {
  return value === null ? 0 : 1;
}

function cycle(value) {
  return value === null ? 0 : 1;
}

function topology(value) {
  return value === null ? 0 : 1;
}

function map(reverse) {
  const value = new Map();
  value.set(reverse ? 'b' : 'a', reverse ? 2 : 1);
  value.set(reverse ? 'a' : 'b', reverse ? 1 : 2);
  return value;
}

function set(reverse) {
  const value = new Set();
  value.add(reverse ? 'b' : 'a');
  value.add(reverse ? 'a' : 'b');
  return value;
}

function stamp(value) {
  return value === null ? 0 : 1;
}

function block(value) {
  return value === null ? 0 : 1;
}

function deep(value) {
  return value === null ? 0 : 1;
}

function longText(value) {
  return value.length;
}

function unreadable(value) {
  return value === null ? 0 : 1;
}

function scalar(value) {
  return value;
}

function throwsNow() {
  throw new Error('reference throw');
}

async function future(value) {
  await Promise.resolve();
  return value;
}

function* unsupportedGenerator() {
  yield 'unsupported';
}

const unsupportedDestructuredArrow = ({ value }) => value;

module.exports = {
  observe,
  noteObservedCall,
  resetObservedCalls,
  observedCalls,
  inspect,
  cycle,
  topology,
  map,
  set,
  stamp,
  block,
  deep,
  longText,
  unreadable,
  scalar,
  throwsNow,
  future,
  unsupportedGenerator,
  unsupportedDestructuredArrow
};