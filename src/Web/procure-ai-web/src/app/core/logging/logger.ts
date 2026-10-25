import { inject, Service } from '@angular/core';
import { AppConfigService, LOG_LEVELS, LogLevel } from '../config/app-config';

export interface LogEntry {
  timestamp: string;
  level: Exclude<LogLevel, 'off'>;
  source: string;
  message: string;
  data?: unknown;
  /** The page path (no query string, which may hold personal data). */
  url: string;
}

type EntryLevel = LogEntry['level'];

/** The same method names on a logger that stamps every entry with its source. */
export interface ScopedLogger {
  trace(message: string, data?: unknown): void;
  debug(message: string, data?: unknown): void;
  info(message: string, data?: unknown): void;
  warn(message: string, data?: unknown): void;
  error(message: string, data?: unknown): void;
}

const SECRET_KEY = /token|password|key|secret|authorization/i;
const MASK = '***';
const MAX_DEPTH = 6;
const MAX_STRING = 1000;
const WINDOW_MS = 60_000;

const rank = (level: LogLevel) => LOG_LEVELS.indexOf(level);

const CONSOLE_METHOD: Record<EntryLevel, 'debug' | 'info' | 'warn' | 'error'> = {
  trace: 'debug',
  debug: 'debug',
  info: 'info',
  warn: 'warn',
  error: 'error',
};

/**
 * Our own logger (ngx-logger is NgModule-based and no longer maintained). Console output with timestamp, level
 * and source; warnings and errors are also batched to the gateway (`serverUrl`), which forwards them to
 * OpenTelemetry. Levels, batch size and timing come from the runtime config. Values under keys that look like
 * secrets are masked before anything leaves the logger.
 */
@Service()
export class Logger implements ScopedLogger {
  private readonly config = inject(AppConfigService).config;
  private buffer: LogEntry[] = [];
  private timer: ReturnType<typeof setTimeout> | undefined;
  private sentAt: number[] = [];

  trace(message: string, data?: unknown): void {
    this.write('trace', 'app', message, data);
  }

  debug(message: string, data?: unknown): void {
    this.write('debug', 'app', message, data);
  }

  info(message: string, data?: unknown): void {
    this.write('info', 'app', message, data);
  }

  warn(message: string, data?: unknown): void {
    this.write('warn', 'app', message, data);
  }

  error(message: string, data?: unknown): void {
    this.write('error', 'app', message, data);
  }

  /** A logger whose entries carry `source` (a class or feature name). */
  withSource(source: string): ScopedLogger {
    return {
      trace: (m, d) => this.write('trace', source, m, d),
      debug: (m, d) => this.write('debug', source, m, d),
      info: (m, d) => this.write('info', source, m, d),
      warn: (m, d) => this.write('warn', source, m, d),
      error: (m, d) => this.write('error', source, m, d),
    };
  }

  /** Sends what is waiting with `sendBeacon`, which still works while the page is being closed. */
  flushOnPageHide(): void {
    const batch = this.takeBatch();
    if (batch.length === 0) {
      return;
    }
    const { serverUrl } = this.config().logger;
    const body = JSON.stringify(batch);
    try {
      if (typeof navigator !== 'undefined' && navigator.sendBeacon?.(serverUrl, new Blob([body], { type: 'application/json' }))) {
        return;
      }
    } catch {
      // Fall through to fetch.
    }
    this.post(serverUrl, body);
  }

  private write(level: EntryLevel, source: string, message: string, data: unknown): void {
    const { logger } = this.config();
    const toConsole = rank(level) >= rank(logger.consoleLevel);
    const toServer = rank(level) >= rank(logger.serverLevel);
    if (!toConsole && !toServer) {
      return;
    }

    const safe = data === undefined ? undefined : sanitize(data, 0);
    const entry: LogEntry = {
      timestamp: new Date().toISOString(),
      level,
      source,
      message,
      ...(safe === undefined ? {} : { data: safe }),
      url: typeof location === 'undefined' ? '' : location.pathname,
    };

    if (toConsole) {
      const line = `${entry.timestamp} ${level.toUpperCase()} [${source}] ${message}`;
      if (safe === undefined) {
        console[CONSOLE_METHOD[level]](line);
      } else {
        console[CONSOLE_METHOD[level]](line, safe);
      }
    }
    if (toServer) {
      this.enqueue(entry);
    }
  }

  private enqueue(entry: LogEntry): void {
    const { batchSize, flushMs, serverMaxPerMinute, serverUrl } = this.config().logger;

    const now = Date.now();
    this.sentAt = this.sentAt.filter((t) => now - t < WINDOW_MS);
    if (this.sentAt.length >= serverMaxPerMinute) {
      return;
    }
    this.sentAt.push(now);

    this.buffer.push(entry);
    if (this.buffer.length >= batchSize) {
      this.post(serverUrl, JSON.stringify(this.takeBatch()));
    } else {
      this.timer ??= setTimeout(() => this.post(serverUrl, JSON.stringify(this.takeBatch())), flushMs);
    }
  }

  private takeBatch(): LogEntry[] {
    clearTimeout(this.timer);
    this.timer = undefined;
    return this.buffer.splice(0);
  }

  /** Fire and forget. Failures are dropped: logging must never cause an error of its own. */
  private post(url: string, body: string): void {
    if (body === '[]') {
      return;
    }
    try {
      fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body, keepalive: true }).catch(() => undefined);
    } catch {
      // Ignored on purpose.
    }
  }
}

/** A copy that is safe to log: secrets masked, errors flattened, depth and string length limited. */
function sanitize(value: unknown, depth: number): unknown {
  if (value instanceof Error) {
    return { name: value.name, message: value.message, stack: value.stack };
  }
  if (typeof value === 'string') {
    return value.length > MAX_STRING ? value.slice(0, MAX_STRING) + '...' : value;
  }
  if (typeof value !== 'object' || value === null) {
    return typeof value === 'function' || typeof value === 'symbol' ? String(value) : value;
  }
  if (depth >= MAX_DEPTH) {
    return '[depth limit]';
  }
  if (Array.isArray(value)) {
    return value.map((item) => sanitize(item, depth + 1));
  }
  return Object.fromEntries(
    Object.entries(value).map(([key, item]) => [key, SECRET_KEY.test(key) ? MASK : sanitize(item, depth + 1)]),
  );
}
