import { computed, DestroyRef, inject, Service, signal } from '@angular/core';
import { AppConfigService } from '../config/app-config';
import { CacheRegistry } from '../state/cache-registry';

export const DEV_PANEL_HEADER = 'X-Dev-Panel';
export const DEV_PANEL_STORAGE_KEY = 'procure.devPanel';
/** The panel keeps this many calls, newest first. */
export const MAX_DEV_CALLS = 50;

export interface DevCall {
  method: string;
  /** Path and query only; values of query parameters that look like secrets are masked. */
  url: string;
  status: number;
  ms: number;
  cache?: { kind: 'hit' | 'miss' | 'bypass'; ageMs?: number };
  /** The server made this call wait for an identical one that was already running. */
  coalesced: boolean;
  /** The server answered from the stored result of an earlier identical POST. */
  replayed: boolean;
  traceId?: string;
  etag?: string;
  tenant?: string;
  policy?: string;
  instance?: string;
  at: number;
}

/** The part of a Headers object we read (HttpHeaders and fetch's Headers both fit). */
export interface HeaderReader {
  get(name: string): string | null;
}

/** Query parameters whose values never reach the panel. */
const SECRET_PARAM = /token|key|secret|password|passwd|auth|code|signature|sig$/i;

/** Strips the origin and masks secret-looking query values. Bodies are never recorded. */
export function safeUrl(url: string): string {
  let path = url;
  try {
    const parsed = new URL(url, 'http://local.invalid');
    parsed.searchParams.forEach((_, name) => {
      if (SECRET_PARAM.test(name)) {
        parsed.searchParams.set(name, '***');
      }
    });
    path = parsed.pathname + (parsed.search ? decodeURIComponent(parsed.search).replace(/ /g, '%20') : '');
  } catch {
    // Keep the text as it is; it was only ever a relative url.
  }
  return path;
}

/** Builds a call from the response headers the server adds when `X-Dev-Panel: 1` was sent (see DevPanelHeaders on the server). */
export function devCallFrom(method: string, url: string, status: number, ms: number, headers: HeaderReader | null, now = Date.now()): DevCall {
  const text = (name: string) => headers?.get(name)?.trim() || undefined;
  const call: DevCall = {
    method: method.toUpperCase(),
    url: safeUrl(url),
    status,
    ms: Math.round(ms),
    coalesced: text('X-Coalesced') === 'joined',
    replayed: text('X-Idempotent') === 'replayed',
    traceId: text('X-Trace-Id'),
    etag: text('ETag'),
    tenant: text('X-Tenant'),
    policy: text('X-Policy'),
    instance: text('X-Instance'),
    at: now,
  };
  const cache = text('X-Cache');
  const kind = cache?.split(';')[0].trim().toLowerCase();
  if (kind === 'hit' || kind === 'miss' || kind === 'bypass') {
    const age = /age-ms=(\d+)/i.exec(cache ?? '');
    call.cache = age ? { kind, ageMs: Number(age[1]) } : { kind };
  }
  return call;
}

/**
 * The "Behind the scenes" panel's state: is it open, and the last calls. Not cached data. Open/closed survives a reload
 * (sessionStorage), the calls do not.
 */
@Service()
export class DevPanelStore {
  private readonly config = inject(AppConfigService);
  readonly open = signal(read());

  constructor() {
    // Logout (and a tenant switch) forgets the calls: their urls carry ids of the previous user's data.
    const unregister = inject(CacheRegistry).register({ clearCache: () => this.clear() });
    inject(DestroyRef).onDestroy(unregister);
  }

  /** The sheet is shown as a one-line bar. */
  readonly collapsed = signal(false);
  readonly calls = signal<DevCall[]>([]);
  /** The feature is switched on in the config. */
  readonly enabled = computed(() => this.config.config().devPanel.enabled);
  /** Calls carry the header and are recorded: enabled and open. */
  readonly active = computed(() => this.enabled() && this.open());

  setOpen(open: boolean): void {
    this.open.set(open);
    try {
      sessionStorage.setItem(DEV_PANEL_STORAGE_KEY, open ? '1' : '0');
    } catch {
      // Storage blocked: the panel still works for this page.
    }
  }

  toggle(): void {
    this.setOpen(!this.open());
  }

  record(call: DevCall): void {
    this.calls.update((all) => [call, ...all].slice(0, MAX_DEV_CALLS));
  }

  clear(): void {
    this.calls.set([]);
  }
}

function read(): boolean {
  try {
    return sessionStorage.getItem(DEV_PANEL_STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}
