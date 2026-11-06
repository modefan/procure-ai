import { DOCUMENT } from '@angular/common';
import { inject, InjectionToken } from '@angular/core';
import { AppConfigService } from '../config/app-config';

/** The path of the Notifications hub behind the gateway. */
export const HUB_PATH = '/hubs/live';

/** The part of a SignalR `HubConnection` that LiveConnection uses, so specs can fake it. */
export interface LiveHub {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
  on(method: string, handler: (...args: any[]) => void): void;
  onreconnecting(callback: () => void): void;
  onreconnected(callback: () => void): void;
  onclose(callback: () => void): void;
}

/**
 * A hub whose SignalR client (`@microsoft/signalr`, about 64 kB) is only loaded when it starts, so it stays out of the initial bundle:
 * the welcome page and the sign-in never need it. Handlers registered before `start` are attached to the real hub once it is built. A
 * failed load rejects `start`; LiveConnection then goes offline and tries again later with a new hub.
 */
export class LazyLiveHub implements LiveHub {
  private loading: Promise<LiveHub> | null = null;
  private real: LiveHub | null = null;
  private readonly pending: ((hub: LiveHub) => void)[] = [];

  constructor(private readonly build: () => Promise<LiveHub>) {}

  start(): Promise<void> {
    return this.load().then((hub) => hub.start());
  }

  async stop(): Promise<void> {
    if (!this.loading) {
      return;
    }
    let hub: LiveHub;
    try {
      hub = await this.loading;
    } catch {
      return;
    }
    await hub.stop();
  }

  invoke(method: string, ...args: unknown[]): Promise<unknown> {
    return this.load().then((hub) => hub.invoke(method, ...args));
  }

  on(method: string, handler: (...args: any[]) => void): void {
    this.attach((hub) => hub.on(method, handler));
  }

  onreconnecting(callback: () => void): void {
    this.attach((hub) => hub.onreconnecting(callback));
  }

  onreconnected(callback: () => void): void {
    this.attach((hub) => hub.onreconnected(callback));
  }

  onclose(callback: () => void): void {
    this.attach((hub) => hub.onclose(callback));
  }

  private load(): Promise<LiveHub> {
    this.loading ??= this.build().then((hub) => {
      this.real = hub;
      for (const register of this.pending.splice(0)) {
        register(hub);
      }
      return hub;
    });
    return this.loading;
  }

  private attach(register: (hub: LiveHub) => void): void {
    if (this.real) {
      register(this.real);
    } else {
      this.pending.push(register);
    }
  }
}

/** Builds a new, not yet started hub. Replaced in specs so none opens a socket. */
export const LIVE_HUB_FACTORY = new InjectionToken<() => LiveHub>('LIVE_HUB_FACTORY', {
  providedIn: 'root',
  factory: () => {
    const document = inject(DOCUMENT);
    const config = inject(AppConfigService);
    return () =>
      new LazyLiveHub(async () => {
        const { HubConnectionBuilder, LogLevel } = await import('@microsoft/signalr');
        const base = config.config().apiBaseUrl;
        const xsrf = readCookie(document.cookie, 'XSRF-TOKEN');
        return new HubConnectionBuilder()
          // Negotiate is a POST through the gateway's BFF, so it carries the anti-forgery header. The WebSocket upgrade
          // cannot carry headers; the session cookie authenticates it.
          .withUrl(`${base}${HUB_PATH}`, { headers: xsrf ? { 'X-XSRF-TOKEN': xsrf } : {}, withCredentials: true })
          .withAutomaticReconnect(config.config().live.reconnectDelaysMs)
          .configureLogging(LogLevel.Warning)
          .build() as unknown as LiveHub;
      });
  },
});

/** Reads one cookie from a `document.cookie` string. */
export function readCookie(cookies: string, name: string): string | null {
  for (const part of cookies.split(';')) {
    const [key, ...rest] = part.trim().split('=');
    if (key === name) {
      return decodeURIComponent(rest.join('='));
    }
  }
  return null;
}
