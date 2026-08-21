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

export function* unsupportedGenerator(): Generator<string, void, unknown> {
  yield 'unsupported';
}

interface DestructuredValue {
  value: unknown;
}

export const unsupportedDestructuredArrow = ({ value }: DestructuredValue): unknown => value;
