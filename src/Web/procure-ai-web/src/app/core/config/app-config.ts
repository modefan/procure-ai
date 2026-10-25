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

export interface AppConfig {
  /** Absolute origin of the gateway when the app is not served by it (development only); empty = same origin. */
  apiBaseUrl: string;
  logger: LoggerConfig;
}

/**
 * Runtime configuration: build-time defaults from environment.ts, overridden by what config.json holds.
 * A signal, so a later change (for example a log level switched in the dev panel) reaches its readers.
 */
@Service()
export class AppConfigService {
  readonly config = signal<AppConfig>(structuredClone(environment));

  /** Loads config.json. Never fails: a missing or broken file leaves the defaults in place. */
  async load(): Promise<void> {
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
  };
}

const isRecord = (value: unknown): value is object => typeof value === 'object' && value !== null && !Array.isArray(value);
const level = (value: unknown, fallback: LogLevel): LogLevel =>
  LOG_LEVELS.includes(value as LogLevel) ? (value as LogLevel) : fallback;
const positive = (value: unknown, fallback: number): number =>
  typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : fallback;

/** Loads config.json before the app finishes starting. List it before every provider that reads the config. */
export function provideAppConfig(): EnvironmentProviders {
  return provideAppInitializer(() => inject(AppConfigService).load());
}
