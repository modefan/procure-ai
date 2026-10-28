import { DOCUMENT } from '@angular/common';
import { inject, InjectionToken } from '@angular/core';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { AppConfigService } from '../config/app-config';

/** The path of the Notifications hub behind the gateway. */
export const HUB_PATH = '/hubs/live';

/** Delays (ms) of the built-in reconnect attempts; after the last one fails the connection is closed. */
export const RECONNECT_DELAYS = [0, 2000, 5000, 10000, 30000];

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

/** Builds a new, not yet started hub. Replaced in specs so none opens a socket. */
export const LIVE_HUB_FACTORY = new InjectionToken<() => LiveHub>('LIVE_HUB_FACTORY', {
  providedIn: 'root',
  factory: () => {
    const document = inject(DOCUMENT);
    const config = inject(AppConfigService);
    return () => {
      const base = config.config().apiBaseUrl;
      const xsrf = readCookie(document.cookie, 'XSRF-TOKEN');
      const hub: HubConnection = new HubConnectionBuilder()
        // Negotiate is a POST through the gateway's BFF, so it carries the anti-forgery header. The WebSocket upgrade
        // cannot carry headers; the session cookie authenticates it.
        .withUrl(`${base}${HUB_PATH}`, { headers: xsrf ? { 'X-XSRF-TOKEN': xsrf } : {}, withCredentials: true })
        .withAutomaticReconnect(RECONNECT_DELAYS)
        .configureLogging(LogLevel.Warning)
        .build();
      return hub as unknown as LiveHub;
    };
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
