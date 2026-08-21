import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { createScope } = require('./src/scope.cjs');
const { transform } = require('./src/transform.cjs');
const scope = createScope();

export async function load(url, context, nextLoad) {
  const loaded = await nextLoad(url, context);
  if (!url.startsWith('file:') || !['module', 'commonjs'].includes(loaded.format)) {
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
  return { ...loaded, source: transform(source, filename).code };
}