import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const require = createRequire(import.meta.url);
const { createScope } = require('./src/scope.cjs');
const { transform } = require('./src/transform.cjs');
const scope = createScope();
const bootstrapImport = pathToFileURL(path.join(path.dirname(fileURLToPath(import.meta.url)), 'bootstrap.mjs')).href;

export async function load(url, context, nextLoad) {
  const loaded = await nextLoad(url, context);
  if (!url.startsWith('file:') || loaded.format !== 'module') {
    return loaded;
  }
  const filename = fileURLToPath(url);
  if (!scope.selects(filename)) {
    return loaded;
  }
  if (loaded.source == null) {
    return loaded;
  }
  const source = typeof loaded.source === 'string' ? loaded.source : Buffer.from(loaded.source).toString('utf8');
  return { ...loaded, source: transform(source, filename, { bootstrapImport }).code };
}