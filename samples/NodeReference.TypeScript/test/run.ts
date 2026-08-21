import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import * as subject from '../src/subject';

type TestCallback = () => void | Promise<void>;

interface BehaviorDiffRuntime {
  withTestRoot(testId: string, callback: TestCallback): void | Promise<void>;
}

interface CycleNode {
  name: string;
  next?: CycleNode;
}

interface DeepNode {
  level: number;
  next?: DeepNode;
}

const runtimeSymbol = Symbol.for('behaviordiff.runtime');
let executed = 0;

async function runCase(testId: string, callback: TestCallback): Promise<void> {
  const runtime = (globalThis as { [key: symbol]: BehaviorDiffRuntime | undefined })[runtimeSymbol];
  const result = runtime && typeof runtime.withTestRoot === 'function'
    ? runtime.withTestRoot(testId, callback)
    : callback();
  await result;
  executed++;
}

function trap(name: string): never {
  subject.noteObservedCall(name);
  throw new Error(`${name} trap ran`);
}

function makeTrappedProxy(label: string): object {
  return new Proxy({}, {
    get() {
      return trap(`${label}.get`);
    },
    ownKeys() {
      return trap(`${label}.ownKeys`);
    },
    getOwnPropertyDescriptor() {
      return trap(`${label}.descriptor`);
    },
    getPrototypeOf() {
      return trap(`${label}.prototype`);
    }
  });
}

async function main(): Promise<void> {
  for (let value = 0; value < 110; value++) {
    const testId = `node-reference/volume/${String(value).padStart(3, '0')}`;
    await runCase(testId, function volumeCase() {
      assert.equal(subject.observe(value), value * 2 + 1);
    });
  }

  await runCase('node-reference/digest-proof', function digestProofCase() {
    subject.resetObservedCalls();
    const sideEffect = {
      get dangerous(): never {
        return trap('getter');
      },
      toString(): never {
        return trap('toString');
      },
      [Symbol.iterator](): never {
        return trap('iterator');
      },
      proxy: makeTrappedProxy('inspect.proxy')
    };
    assert.equal(subject.inspect(sideEffect), 1);
    assert.equal(subject.observedCalls(), '');

    const firstCycle: CycleNode = { name: 'same' };
    firstCycle.next = firstCycle;
    const secondCycle: CycleNode = { name: 'same' };
    secondCycle.next = secondCycle;
    assert.strictEqual(firstCycle.next, firstCycle);
    assert.strictEqual(secondCycle.next, secondCycle);
    assert.equal(subject.cycle(firstCycle), 1);
    assert.equal(subject.cycle(secondCycle), 1);

    const sharedChild = { value: 7 };
    const shared = { left: sharedChild, right: sharedChild };
    const copies = { left: { value: 7 }, right: { value: 7 } };
    assert.strictEqual(shared.left, shared.right);
    assert.notStrictEqual(copies.left, copies.right);
    assert.equal(subject.topology(shared), 1);
    assert.equal(subject.topology(copies), 1);

    assert.deepEqual(subject.map(false), subject.map(true));
    assert.deepEqual(subject.set(false), subject.set(true));

    const firstStamp = { id: Symbol('first'), at: new Date(0), name: 'fixed-semantic-name' };
    const secondStamp = { id: Symbol('second'), at: new Date(1_000), name: 'fixed-semantic-name' };
    assert.equal(firstStamp.name, secondStamp.name);
    assert.notEqual(firstStamp.id, secondStamp.id);
    assert.notEqual(firstStamp.at.getTime(), secondStamp.at.getTime());
    assert.equal(subject.stamp(firstStamp), 1);
    assert.equal(subject.stamp(secondStamp), 1);

    const blocked = { proxy: makeTrappedProxy('block.proxy') };
    Object.defineProperty(blocked, 'accessor', {
      enumerable: true,
      get() {
        return trap('block.accessor');
      }
    });
    assert.equal(subject.block(blocked), 1);

    const deepValue: DeepNode = { level: 0 };
    let cursor = deepValue;
    for (let level = 1; level <= 9; level++) {
      cursor.next = { level };
      cursor = cursor.next;
    }
    assert.equal(cursor.level, 9);
    assert.equal(subject.deep(deepValue), 1);

    const longPrefix = 'a'.repeat(3_100);
    assert.equal(subject.longText(`${longPrefix}x`), 3_101);
    assert.equal(subject.longText(`${longPrefix}y`), 3_101);

    const unreadableValue = { visible: 'retained', region: makeTrappedProxy('unreadable.proxy') };
    Object.defineProperty(unreadableValue, 'secret', {
      enumerable: true,
      get() {
        return trap('unreadable.accessor');
      }
    });
    assert.equal(subject.unreadable(unreadableValue), 1);
    assert.equal(subject.observedCalls(), '');

    const scalars = [undefined, null, NaN, Infinity, -Infinity, -0, 0, 1n, '1'];
    for (const value of scalars) {
      assert.ok(Object.is(subject.scalar(value), value));
    }
  });

  await runCase('node-reference/exceptional', function exceptionalCase() {
    assert.throws(subject.throwsNow, /reference throw/);
  });

  await runCase('node-reference/async', async function asyncCase() {
    assert.equal(await subject.future('settled'), 'settled');
  });

  assert.equal(executed, 113);
  const report = { runnerTests: executed };
  const serialized = `${JSON.stringify(report)}\n`;
  if (process.env.BEHAVIORDIFF_RUNNER_REPORT) {
    const reportPath = path.resolve(process.env.BEHAVIORDIFF_RUNNER_REPORT);
    fs.mkdirSync(path.dirname(reportPath), { recursive: true });
    fs.writeFileSync(reportPath, serialized, 'utf8');
  }
  process.stdout.write(serialized);
}

main().catch(function reportFailure(error: unknown) {
  const rendered = error instanceof Error ? error.stack || error.message : String(error);
  process.stderr.write(`${rendered}\n`);
  process.exitCode = 1;
});