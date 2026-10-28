import type { AppConfig } from '../app/core/config/app-config';

/**
 * Build-time defaults. public/config.json overrides them at runtime (one build runs in every environment);
 * a key missing from config.json keeps the value below. Nothing secret belongs here: this ships to every browser.
 */
export const environment: AppConfig = {
  // Empty = same origin: the browser only ever talks to the gateway that served the app.
  apiBaseUrl: '',
  logger: {
    consoleLevel: 'debug',
    serverLevel: 'warn',
    serverUrl: '/api/client-logs',
    batchSize: 10,
    flushMs: 5000,
    serverMaxPerMinute: 60,
  },
  // Rows per page in lists (the server allows at most its MaxPageSize).
  paging: { pageSize: 25 },
};
