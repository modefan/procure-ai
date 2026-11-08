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
  live: {
    // Server: Presence:HeartbeatInterval 15 s, Presence:Ttl 45 s. Stay under the TTL.
    heartbeatMs: 15_000,
    reconnectDelaysMs: [0, 2000, 5000, 10000, 30000],
    offlineRetryMs: 30_000,
  },
  cache: {
    staleMs: 30_000,
    trailingReloadMs: 2000,
  },
  // The local Aspire dashboard (AppHost launchSettings `https` profile). Deployments set their own in config.json.
  observability: { dashboardUrl: 'https://localhost:17100' },
  // Off by default; local and demo set it in config.json (the server needs DevPanel:Enabled too).
  devPanel: { enabled: false },
  // The maintenance page's "request access" link. Empty: no link (deployments set their own in config.json).
  maintenance: { contactUrl: '', contactLabel: '' },
};
