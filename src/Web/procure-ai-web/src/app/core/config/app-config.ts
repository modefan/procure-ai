import { inject, provideAppInitializer, Service, signal, EnvironmentProviders } from '@angular/core';
import { environment } from '../../../environments/environment';

export type LogLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error' | 'off';

export const LOG_LEVELS: readonly LogLevel[] = ['trace', 'debug', 'info', 'warn', 'error', 'off'];

export interface LoggerConfig {
  consoleLevel: LogLevel;
  serverLevel: LogLevel;
  serverUrl: string;
  batchSize: number;
  flushMs: number;
  /** Most entries sent to the server per minute; the rest are dropped. */
  serverMaxPerMinute: number;
}

/** Live updates (SignalR). */
export interface LiveConfig {
  /**
   * How often a connected client tells the server it is still there, in ms. Keep it well under the server's
   * `Presence:Ttl` (45 s) and near its `Presence:HeartbeatInterval` (15 s): a silent client drops off "viewing" after the TTL.
   */
  heartbeatMs: number;
  /** Delays (ms) of the built-in reconnect attempts; after the last one fails the connection is closed. */
  reconnectDelaysMs: number[];
  /** After the reconnect attempts ran out ("Offline"), how long to wait before connecting again, in ms. */
  offlineRetryMs: number;
}

/** In-memory store caches. */
export interface CacheConfig {
  /** How long a loaded list or detail counts as fresh, in ms (reference data is session-long). */
  staleMs: number;
  /**
   * A push can arrive before another service has written the change it announces (Notifications and that service read the same
   * event). The stores that show such data read again this long after the push, in ms.
   */
  trailingReloadMs: number;
}

/** Where the observability tools live. */
export interface ObservabilityConfig {
  /** The Aspire dashboard (traces, logs, metrics) the system status page links to; empty = no link. */
  dashboardUrl: string;
}

/** The "Behind the scenes" panel (cache, coalescing and trace details of the last API calls). */
export interface DevPanelConfig {
  /** Show the panel's menu item and shortcut. The server must also have `DevPanel:Enabled` for the headers to come back. */
  enabled: boolean;
}

/** The maintenance page (shown while the demo is switched off). */
export interface MaintenanceConfig {
  /** Where a visitor can ask for the demo to be switched on (https:, http: or mailto:); empty = no contact link. */
  contactUrl: string;
  /** The text of that link; empty = "Request access". */
  contactLabel: string;
  /**
   * How long the start-up (and "Try again") read of the switch may take, in ms. After that the demo counts as on (fail open, as in the
   * gateway): a hung Access must not hold the app's start for the gateway's whole proxy timeout.
   */
  checkTimeoutMs: number;
}

export interface AppConfig {
  /** Absolute origin of the gateway when the app is not served by it (development only); empty = same origin. */
  apiBaseUrl: string;
  logger: LoggerConfig;
  paging: { pageSize: number };
  live: LiveConfig;
  cache: CacheConfig;
  observability: ObservabilityConfig;
  devPanel: DevPanelConfig;
  maintenance: MaintenanceConfig;
}

/**
 * Runtime configuration: build-time defaults from environment.ts, overridden by what config.json holds.
 * A signal, so a later change (for example a log level switched in the dev panel) reaches its readers.
 */
@Service()
export class AppConfigService {
  readonly config = signal<AppConfig>(structuredClone(environment));
  private loading: Promise<void> | null = null;

  /**
   * Loads config.json (once: later calls share the first load, so an initializer that needs the config can wait for it). Never fails:
   * a missing or broken file leaves the defaults in place.
   */
  load(): Promise<void> {
    return (this.loading ??= this.fetchConfig());
  }

  private async fetchConfig(): Promise<void> {
    try {
      const response = await fetch('config.json', { cache: 'no-store' });
      if (!response.ok) {
        return;
      }
      this.config.set(merge(environment, await response.json()));
    } catch {
      // Defaults stay. The logger is not up yet, and the app can run without the file.
    }
  }
}

/** Takes from `loaded` only the keys that exist in the defaults and have the right type. */
export function merge(defaults: AppConfig, loaded: unknown): AppConfig {
  const source = (isRecord(loaded) ? loaded : {}) as Record<string, unknown>;
  const logger = (isRecord(source['logger']) ? source['logger'] : {}) as Record<string, unknown>;
  const paging = (isRecord(source['paging']) ? source['paging'] : {}) as Record<string, unknown>;
  const live = (isRecord(source['live']) ? source['live'] : {}) as Record<string, unknown>;
  const cache = (isRecord(source['cache']) ? source['cache'] : {}) as Record<string, unknown>;
  const observability = (isRecord(source['observability']) ? source['observability'] : {}) as Record<string, unknown>;
  const devPanel = (isRecord(source['devPanel']) ? source['devPanel'] : {}) as Record<string, unknown>;
  const maintenance = (isRecord(source['maintenance']) ? source['maintenance'] : {}) as Record<string, unknown>;
  const d = defaults.logger;
  return {
    apiBaseUrl: typeof source['apiBaseUrl'] === 'string' ? source['apiBaseUrl'] : defaults.apiBaseUrl,
    logger: {
      consoleLevel: level(logger['consoleLevel'], d.consoleLevel),
      serverLevel: level(logger['serverLevel'], d.serverLevel),
      serverUrl: typeof logger['serverUrl'] === 'string' && logger['serverUrl'] ? logger['serverUrl'] : d.serverUrl,
      batchSize: positive(logger['batchSize'], d.batchSize),
      flushMs: positive(logger['flushMs'], d.flushMs),
      serverMaxPerMinute: positive(logger['serverMaxPerMinute'], d.serverMaxPerMinute),
    },
    paging: { pageSize: positive(paging['pageSize'], defaults.paging.pageSize) },
    live: {
      heartbeatMs: positive(live['heartbeatMs'], defaults.live.heartbeatMs),
      reconnectDelaysMs: delays(live['reconnectDelaysMs'], defaults.live.reconnectDelaysMs),
      offlineRetryMs: positive(live['offlineRetryMs'], defaults.live.offlineRetryMs),
    },
    cache: {
      staleMs: positive(cache['staleMs'], defaults.cache.staleMs),
      trailingReloadMs: positive(cache['trailingReloadMs'], defaults.cache.trailingReloadMs),
    },
    observability: {
      dashboardUrl: typeof observability['dashboardUrl'] === 'string' ? observability['dashboardUrl'] : defaults.observability.dashboardUrl,
    },
    devPanel: { enabled: typeof devPanel['enabled'] === 'boolean' ? devPanel['enabled'] : defaults.devPanel.enabled },
    maintenance: {
      contactUrl: contactUrl(maintenance['contactUrl'], defaults.maintenance.contactUrl),
      contactLabel: typeof maintenance['contactLabel'] === 'string' ? maintenance['contactLabel'].trim() : defaults.maintenance.contactLabel,
      checkTimeoutMs: positive(maintenance['checkTimeoutMs'], defaults.maintenance.checkTimeoutMs),
    },
  };
}

const isRecord = (value: unknown): value is object => typeof value === 'object' && value !== null && !Array.isArray(value);
const level = (value: unknown, fallback: LogLevel): LogLevel =>
  LOG_LEVELS.includes(value as LogLevel) ? (value as LogLevel) : fallback;
const positive = (value: unknown, fallback: number): number =>
  typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : fallback;

const delays = (value: unknown, fallback: number[]): number[] =>
  Array.isArray(value) && value.length > 0 && value.every((v) => typeof v === 'number' && Number.isFinite(v) && v >= 0)
    ? (value as number[])
    : fallback;

/** Only a link a visitor can safely follow: https:, http: or mailto:. Anything else (javascript:, relative) keeps the default. */
const contactUrl = (value: unknown, fallback: string): string =>
  typeof value === 'string' && /^(https?:\/\/|mailto:)\S+$/i.test(value.trim()) ? value.trim() : value === '' ? '' : fallback;

/** Loads config.json before the app finishes starting. List it before every provider that reads the config. */
export function provideAppConfig(): EnvironmentProviders {
  return provideAppInitializer(() => inject(AppConfigService).load());
}
