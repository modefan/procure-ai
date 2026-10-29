import { environment } from '../../../environments/environment';
import { merge } from './app-config';

describe('merge (live and cache settings)', () => {
  it('keeps the defaults when config.json has no live or cache section', () => {
    const merged = merge(environment, {});

    expect(merged.live).toEqual(environment.live);
    expect(merged.cache).toEqual(environment.cache);
  });

  it('takes the values config.json gives', () => {
    const merged = merge(environment, {
      live: { heartbeatMs: 10000, reconnectDelaysMs: [0, 1000], offlineRetryMs: 60000 },
      cache: { staleMs: 5000, trailingReloadMs: 500 },
    });

    expect(merged.live).toEqual({ heartbeatMs: 10000, reconnectDelaysMs: [0, 1000], offlineRetryMs: 60000 });
    expect(merged.cache).toEqual({ staleMs: 5000, trailingReloadMs: 500 });
  });

  it('ignores values of the wrong type or range and keeps the default for them', () => {
    const merged = merge(environment, {
      live: { heartbeatMs: -1, reconnectDelaysMs: [], offlineRetryMs: '30' },
      cache: { staleMs: 0, trailingReloadMs: null },
    });

    expect(merged.live).toEqual(environment.live);
    expect(merged.cache).toEqual(environment.cache);
    expect(merge(environment, { live: { reconnectDelaysMs: [0, 'x'] } }).live.reconnectDelaysMs).toEqual(environment.live.reconnectDelaysMs);
  });

  it('the heartbeat default stays under the server TTL of 45 s', () => {
    expect(environment.live.heartbeatMs).toBeLessThan(45_000);
  });
});
