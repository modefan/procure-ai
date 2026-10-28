import type { LiveHub } from './hub-connection';

/** A hub for specs: no socket. Tests drive its lifecycle and push server messages with `emit`. */
export class FakeHub implements LiveHub {
  readonly invoked: { method: string; args: unknown[] }[] = [];
  readonly handlers = new Map<string, ((...args: unknown[]) => void)[]>();
  startResult: () => Promise<void> = () => Promise.resolve();
  invokeResult: (method: string, args: unknown[]) => Promise<unknown> = () => Promise.resolve([]);
  stopped = false;
  private reconnecting: (() => void)[] = [];
  private reconnected: (() => void)[] = [];
  private closed: (() => void)[] = [];

  start(): Promise<void> {
    return this.startResult();
  }
  stop(): Promise<void> {
    this.stopped = true;
    return Promise.resolve();
  }
  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    this.invoked.push({ method, args });
    return this.invokeResult(method, args);
  }
  on(method: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(method, [...(this.handlers.get(method) ?? []), handler]);
  }
  onreconnecting(cb: () => void): void {
    this.reconnecting.push(cb);
  }
  onreconnected(cb: () => void): void {
    this.reconnected.push(cb);
  }
  onclose(cb: () => void): void {
    this.closed.push(cb);
  }

  /** The server sends `method`. */
  emit(method: string, ...args: unknown[]): void {
    (this.handlers.get(method) ?? []).forEach((h) => h(...args));
  }
  dropConnection(): void {
    this.reconnecting.forEach((cb) => cb());
  }
  reconnectOk(): void {
    this.reconnected.forEach((cb) => cb());
  }
  giveUp(): void {
    this.closed.forEach((cb) => cb());
  }
  invokedMethods(): string[] {
    return this.invoked.map((i) => i.method);
  }
}

/** Provider-ready factory that records every hub it builds; `configure` runs on each new hub (e.g. to make start fail). */
export function fakeHubFactory(configure?: (hub: FakeHub, index: number) => void) {
  const hubs: FakeHub[] = [];
  const factory = () => {
    const hub = new FakeHub();
    configure?.(hub, hubs.length);
    hubs.push(hub);
    return hub;
  };
  return { hubs, factory };
}
