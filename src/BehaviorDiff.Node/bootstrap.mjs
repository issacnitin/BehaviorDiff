import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { initializeRuntime } = require('./src/runtime.cjs');

initializeRuntime();