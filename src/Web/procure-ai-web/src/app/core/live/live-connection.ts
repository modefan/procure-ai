import { inject, Service, signal } from '@angular/core';
import { AppConfigService } from '../config/app-config';
import { Logger } from '../logging/logger';
import { CacheRegistry } from '../state/cache-registry';
import { LIVE_HUB_FACTORY, LiveHub } from './hub-connection';
import { EntityChanged, Viewer } from './live.models';

export type LiveState = 'connecting' | 'connected' | 'reconnecting' | 'offline';

/** Server-to-client methods the hub sends. */
const SERVER_METHODS = ['EntityChanged', 'ViewersChanged', 'OnlineChanged', 'NotificationAdded'] as const;

type Handler = (...args: any[]) => void;

/**
 * The one SignalR connection to the Notifications hub. Started after sign-in, stopped at logout.
 *
 * - `state` follows the lifecycle; the header shows it.
 * - After SignalR's built-in retries (`live.reconnectDelaysMs` in config.json: 0, 2, 5, 10, 30 s) give up, the state is `offline`
 *   and a new connection is tried every `live.offlineRetryMs` (30 s) until it works or `stop()` is called.
 * - Handlers belong to this service, not to a hub: they keep working when a new hub replaces a dead one.
 * - `start()` resolves before the server has put the connection in its groups, so a push right after can be missed.
 *   After **every** connect (the first one too) and reconnect the registered stores revalidate and every
 *   `onReconnected` callback runs (presence re-sends its watches there).
 */
@Service()
export class LiveConnection {
  private readonly factory = inject(LIVE_HUB_FACTORY);
  private readonly caches = inject(CacheRegistry);
  private readonly config = inject(AppConfigService);
  private readonly log = inject(Logger).withSource('LiveConnection');

  private readonly _state = signal<LiveState>('offline');
  readonly state = this._state.asReadonly();

  private hub: LiveHub | null = null;
  private running = false;
  /** Changes at every stop, so a start or retry from an earlier session cannot act after it. */
  private session = 0;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly handlers = new Map<string, Set<Handler>>();
  private readonly connectedHandlers = new Set<() => void>();

  /** Opens the connection (does nothing if already open or opening). Not awaited: watch `state`. */
  start(): void {
    if (this.running) {
      return;
    }
    this.running = true;
    this._state.set('connecting');
    void this.connect(this.session);
  }

  /** Closes the connection and cancels retries. Used at logout. */
  async stop(): Promise<void> {
    this.running = false;
    this.session++;
    this.clearRetry();
    const hub = this.hub;
    this.hub = null;
    this._state.set('offline');
    if (hub) {
      try {
        await hub.stop();
      } catch (error) {
        this.log.debug('Stopping the hub failed', error);
      }
    }
  }

  /** Calls a hub method. Rejects while not connected (a dropped connection forgets watches itself). */
  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    if (!this.hub || this._state() !== 'connected') {
      return Promise.reject(new Error('The live connection is not open.'));
    }
    return this.hub.invoke(method, ...args);
  }

  /** Listens to one server message. Returns the function that removes the listener. */
  on<T extends unknown[]>(method: string, handler: (...args: T) => void): () => void {
    const set = this.handlers.get(method) ?? new Set<Handler>();
    this.handlers.set(method, set);
    set.add(handler as Handler);
    return () => {
      set.delete(handler as Handler);
    };
  }

  /** Listens to `EntityChanged` pushes of one entity kind (`request`, `approval`, `budget`, `order`, `invoice`). */
  onEntityChanged(entity: string, handler: (change: EntityChanged) => void): () => void {
    return this.on<[EntityChanged]>('EntityChanged', (change) => {
      if (change.entity === entity) {
        handler(change);
      }
    });
  }

  onViewersChanged(handler: (entity: string, id: string, viewers: Viewer[]) => void): () => void {
    return this.on('ViewersChanged', handler);
  }

  /** Runs after every successful connect and reconnect. */
  onReconnected(handler: () => void): () => void {
    this.connectedHandlers.add(handler);
    return () => {
      this.connectedHandlers.delete(handler);
    };
  }

  private async connect(session: number): Promise<void> {
    const hub = this.factory();
    for (const method of SERVER_METHODS) {
      hub.on(method, (...args: unknown[]) => {
        if (this.hub === hub) {
          this.dispatch(method, args);
        }
      });
    }
    hub.onreconnecting(() => {
      if (this.hub === hub) {
        this._state.set('reconnecting');
      }
    });
    hub.onreconnected(() => {
      if (this.hub === hub) {
        this.connected();
      }
    });
    hub.onclose(() => {
      if (this.hub === hub && this.running) {
        this.hub = null;
        this.goOffline(session);
      }
    });
    this.hub = hub;
    try {
      await hub.start();
    } catch (error) {
      if (session === this.session) {
        this.log.warn('The live connection could not be opened', error);
        this.hub = null;
        this.goOffline(session);
      }
      return;
    }
    if (session !== this.session) {
      // Stopped while connecting: close what just opened.
      void hub.stop().catch(() => undefined);
      return;
    }
    this.connected();
  }

  private connected(): void {
    this._state.set('connected');
    try {
      this.caches.revalidateAll();
    } catch (error) {
      this.log.warn('Revalidating the stores failed', error);
    }
    for (const handler of [...this.connectedHandlers]) {
      try {
        handler();
      } catch (error) {
        this.log.warn('A reconnect handler failed', error);
      }
    }
  }

  private goOffline(session: number): void {
    this._state.set('offline');
    this.clearRetry();
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      if (this.running && session === this.session) {
        void this.connect(session);
      }
    }, this.config.config().live.offlineRetryMs);
  }

  private clearRetry(): void {
    if (this.retryTimer !== null) {
      clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
  }

  private dispatch(method: string, args: unknown[]): void {
    for (const handler of [...(this.handlers.get(method) ?? [])]) {
      try {
        handler(...args);
      } catch (error) {
        this.log.warn(`A handler of ${method} failed`, error);
      }
    }
  }
}
