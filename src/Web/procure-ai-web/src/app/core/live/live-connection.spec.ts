import { TestBed } from '@angular/core/testing';
import { CacheRegistry } from '../state/cache-registry';
import { FakeHub, fakeHubFactory } from './fake-hub.testing';
import { LIVE_HUB_FACTORY } from './hub-connection';
import { LiveConnection } from './live-connection';

describe('LiveConnection', () => {
  let hubs: FakeHub[];
  let live: LiveConnection;
  let revalidateAll: ReturnType<typeof vi.fn>;
  /** Hubs built from now on (index >= failFrom) fail to start. */
  let failFrom: number;

  beforeEach(() => {
    vi.useFakeTimers();
    revalidateAll = vi.fn();
    failFrom = Number.POSITIVE_INFINITY;
    const fake = fakeHubFactory((hub, index) => {
      if (index >= failFrom) {
        hub.startResult = () => Promise.reject(new Error('down'));
      }
    });
    hubs = fake.hubs;
    TestBed.configureTestingModule({
      providers: [
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
        { provide: CacheRegistry, useValue: { revalidateAll, register: () => () => undefined, clearAll: () => undefined } },
      ],
    });
    live = TestBed.inject(LiveConnection);
  });

  afterEach(() => {
    void live.stop();
    vi.useRealTimers();
  });

  const settle = () => vi.advanceTimersByTimeAsync(0);
  const change = { entity: 'request', id: 'r1', requestId: null, eventType: 'x', occurredAt: 't' };

  it('state_follows_connection_lifecycle', async () => {
    expect(live.state()).toBe('offline');

    live.start();
    expect(live.state()).toBe('connecting');
    await settle();
    expect(live.state()).toBe('connected');

    hubs[0].dropConnection();
    expect(live.state()).toBe('reconnecting');

    hubs[0].reconnectOk();
    expect(live.state()).toBe('connected');
  });

  it('start_twice_opens_one_connection', async () => {
    live.start();
    live.start();
    await settle();
    expect(hubs).toHaveLength(1);
  });

  it('reconnect_rewatches_and_revalidates_all_stores', async () => {
    const connected = vi.fn();
    live.onReconnected(connected);
    live.start();
    await settle();
    expect(revalidateAll).toHaveBeenCalledTimes(1);
    expect(connected).toHaveBeenCalledTimes(1);

    hubs[0].dropConnection();
    hubs[0].reconnectOk();

    expect(revalidateAll).toHaveBeenCalledTimes(2);
    expect(connected).toHaveBeenCalledTimes(2);
  });

  it('gives_up_then_retries_every_30s', async () => {
    live.start();
    await settle();
    failFrom = 1; // every attempt after the first connection fails
    hubs[0].giveUp();
    expect(live.state()).toBe('offline');

    await vi.advanceTimersByTimeAsync(29_000);
    expect(hubs).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(1_000);
    expect(hubs).toHaveLength(2);
    expect(live.state()).toBe('offline');

    await vi.advanceTimersByTimeAsync(30_000);
    expect(hubs).toHaveLength(3);
    expect(live.state()).toBe('offline');

    failFrom = Number.POSITIVE_INFINITY;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(hubs).toHaveLength(4);
    expect(live.state()).toBe('connected');
    await vi.advanceTimersByTimeAsync(120_000);
    expect(hubs).toHaveLength(4);
  });

  it('a_first_start_that_fails_goes_offline_and_retries', async () => {
    failFrom = 0;
    live.start();
    await settle();
    expect(live.state()).toBe('offline');

    failFrom = Number.POSITIVE_INFINITY;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(live.state()).toBe('connected');
  });

  it('unsubscribe_removes_handler', async () => {
    const handler = vi.fn();
    const off = live.onEntityChanged('request', handler);
    const other = vi.fn();
    live.onEntityChanged('budget', other);
    live.start();
    await settle();

    hubs[0].emit('EntityChanged', change);
    expect(handler).toHaveBeenCalledTimes(1);
    expect(other).not.toHaveBeenCalled();

    off();
    hubs[0].emit('EntityChanged', change);
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('handlers_survive_a_new_hub_and_a_dead_hub_is_ignored', async () => {
    const handler = vi.fn();
    live.onEntityChanged('request', handler);
    live.start();
    await settle();
    hubs[0].giveUp();
    await vi.advanceTimersByTimeAsync(30_000);

    hubs[1].emit('EntityChanged', change);
    expect(handler).toHaveBeenCalledTimes(1);
    hubs[0].emit('EntityChanged', change);
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('invoke_works_only_while_connected', async () => {
    await expect(live.invoke('Heartbeat')).rejects.toThrow();
    live.start();
    await settle();
    await live.invoke('Watch', 'request', 'r1');
    expect(hubs[0].invoked).toEqual([{ method: 'Watch', args: ['request', 'r1'] }]);
    hubs[0].dropConnection();
    await expect(live.invoke('Heartbeat')).rejects.toThrow();
  });

  it('stop_closes_the_hub_cancels_retries_and_goes_offline', async () => {
    live.start();
    await settle();
    await live.stop();
    expect(hubs[0].stopped).toBe(true);
    expect(live.state()).toBe('offline');

    hubs[0].giveUp(); // a late close from the old hub must not start a retry
    await vi.advanceTimersByTimeAsync(60_000);
    expect(hubs).toHaveLength(1);
    expect(live.state()).toBe('offline');
  });

  it('a_stop_while_connecting_closes_the_connection_that_finishes_later', async () => {
    let finish!: () => void;
    TestBed.resetTestingModule();
    const fake = fakeHubFactory((hub) => {
      hub.startResult = () => new Promise<void>((resolve) => (finish = resolve));
    });
    TestBed.configureTestingModule({
      providers: [
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
        { provide: CacheRegistry, useValue: { revalidateAll, register: () => () => undefined, clearAll: () => undefined } },
      ],
    });
    const slow = TestBed.inject(LiveConnection);
    slow.start();
    await slow.stop();
    finish();
    await settle();

    expect(slow.state()).toBe('offline');
    expect(fake.hubs[0].stopped).toBe(true);
    expect(revalidateAll).not.toHaveBeenCalled();
  });

  it('start_after_stop_connects_again', async () => {
    live.start();
    await settle();
    await live.stop();
    live.start();
    await settle();
    expect(hubs).toHaveLength(2);
    expect(live.state()).toBe('connected');
  });
});
