const observed: string[] = [];

export function observe(value: number): number {
  return value * 2 + 1;
}

export function noteObservedCall(name: string): void {
  observed.push(name);
}

export function resetObservedCalls(): void {
  observed.length = 0;
}

export function observedCalls(): string {
  return observed.join(',');
}

export function inspect(value: unknown): number {
  return value === null ? 0 : 1;
}

export function cycle(value: unknown): number {
  return value === null ? 0 : 1;
}

export function topology(value: unknown): number {
  return value === null ? 0 : 1;
}

export function map(reverse: boolean): Map<string, number> {
  const value = new Map<string, number>();
  value.set(reverse ? 'b' : 'a', reverse ? 2 : 1);
  value.set(reverse ? 'a' : 'b', reverse ? 1 : 2);
  return value;
}

export function set(reverse: boolean): Set<string> {
  const value = new Set<string>();
  value.add(reverse ? 'b' : 'a');
  value.add(reverse ? 'a' : 'b');
  return value;
}

export function stamp(value: unknown): number {
  return value === null ? 0 : 1;
}

export function block(value: unknown): number {
  return value === null ? 0 : 1;
}

export function deep(value: unknown): number {
  return value === null ? 0 : 1;
}

export function longText(value: string): number {
  return value.length;
}

export function unreadable(value: unknown): number {
  return value === null ? 0 : 1;
}

export function scalar<T>(value: T): T {
  return value;
}

export function throwsNow(): never {
  throw new Error('reference throw');
}

export async function future<T>(value: T): Promise<T> {
  await Promise.resolve();
  return value;
}

export class BaseFormatter {
  constructor(private readonly prefix: string) {}

  describe(value: string): string {
    return `${this.prefix}:${value}`;
  }

  decorate(value: string): string {
    return `[${this.describe(value)}]`;
  }
}

export class DerivedFormatter extends BaseFormatter {
  override describe(value: string): string {
    return super.describe(value).toUpperCase();
  }
}

export function dispatchFormatter(formatter: BaseFormatter, value: string): string {
  return formatter.decorate(value);
}

export class ValueBox {
  constructor(private readonly value: number) {}

  current(): number {
    return this.value;
  }

  scale(factor: number): number {
    return this.value * factor;
  }

  static create(value: number): ValueBox {
    return new ValueBox(value);
  }
}

export function dispatchValueBox(value: number): { current: number; scaled: number } {
  const box = ValueBox.create(value);
  return { current: box.current(), scaled: box.scale(3) };
}

const arrowIncrement = (value: number): number => value + 1;
const arrowMultiply = (value: number, factor: number): number => value * factor;
const arrowLabel = (prefix: string, value: number): string => `${prefix}:${value}`;

export function dispatchArrows(value: number): string {
  return arrowLabel('arrow', arrowMultiply(arrowIncrement(value), 3));
}

const objectPipeline = {
  normalize(value: string): string {
    return value.trim().toLowerCase();
  },
  decorate(value: string): string {
    return `<${this.normalize(value)}>`;
  },
  summarize(value: string): { text: string; length: number } {
    return { text: this.decorate(value), length: this.normalize(value).length };
  }
};

export function dispatchObjectPipeline(value: string): { text: string; length: number } {
  return objectPipeline.summarize(value);
}

function createClosurePipeline(offset: number, multiplier: number): (value: number) => number {
  function addOffset(value: number): number {
    return value + offset;
  }

  function multiply(value: number): number {
    return value * multiplier;
  }

  function apply(value: number): number {
    return multiply(addOffset(value));
  }

  return apply;
}

export function dispatchClosurePipeline(value: number, offset: number, multiplier: number): number {
  return createClosurePipeline(offset, multiplier)(value);
}

function requireNonNegative(value: number): number {
  if (value < 0) {
    throw new RangeError(`negative reading: ${value}`);
  }
  return value;
}

function isEven(value: number): boolean {
  return value % 2 === 0;
}

function doubleReading(value: number): number {
  return value * 2;
}

export function processReadings(values: number[]): { values: number[]; recovered: number } {
  const accepted: number[] = [];
  let recovered = 0;
  for (const value of values) {
    try {
      accepted.push(requireNonNegative(value));
    } catch (error) {
      if (!(error instanceof RangeError)) {
        throw error;
      }
      recovered++;
    }
  }
  return { values: accepted.filter(isEven).map(doubleReading), recovered };
}

class AsyncSettlement {
  constructor(private readonly suffix: string) {}

  async settle(value: number): Promise<string> {
    await Promise.resolve();
    return `${value}:${this.suffix}`;
  }
}

function incrementPromise(value: number): number {
  return value + 1;
}

function labelPromise(value: string): string {
  return `settled=${value}`;
}

export function promiseWorkflow(value: number): Promise<string> {
  const settlement = new AsyncSettlement('done');
  function settleValue(next: number): Promise<string> {
    return settlement.settle(next);
  }
  return Promise.resolve(value)
    .then(incrementPromise)
    .then(settleValue)
    .then(labelPromise);
}

export function* unsupportedGenerator(): Generator<string, void, unknown> {
  yield 'unsupported';
}

interface DestructuredValue {
  value: unknown;
}

export const unsupportedDestructuredArrow = ({ value }: DestructuredValue): unknown => value;
