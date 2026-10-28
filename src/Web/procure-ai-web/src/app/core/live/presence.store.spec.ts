import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AuthStore } from '../auth/auth.store';
import { CacheRegistry } from '../state/cache-registry';
import { FakeHub, fakeHubFactory } from './fake-hub.testing';
import { LIVE_HUB_FACTORY } from './hub-connection';
import { LiveConnection } from './live-connection';
import { PresenceStore } from './presence.store';

const me = { userId: 'me', name: 'Me' };
const ben = { userId: 'ben', name: 'Ben' };
const maria = { userId: 'maria', name: 'Maria' };

describe('PresenceStore', () => {
  let hubs: FakeHub[];
  let live: LiveConnection;
  let presence: InstanceType<typeof PresenceStore>;

  beforeEach(() => {
    vi.useFakeTimers();
    const fake = fakeHubFactory((hub) => {
      hub.invokeResult = (method) => Promise.resolve(method === 'Watch' ? [me, ben] : undefined);
    });
    hubs = fake.hubs;
    TestBed.configureTestingModule({
      providers: [
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
        { provide: AuthStore, useValue: { user: signal({ userId: 'me', name: 'Me' }) } },
      ],
    });
    live = TestBed.inject(LiveConnection);
    presence = TestBed.inject(PresenceStore);
  });

  afterEach(() => {
    void live.stop();
    vi.useRealTimers();
  });

  const connect = async () => {
    live.start();
    await vi.advanceTimersByTimeAsync(0);
    TestBed.tick();
  };

  it('viewers_exclude_me', async () => {
    await connect();
    presence.watch('request', 'r1');
    await vi.advanceTimersByTimeAsync(0);
    hubs[0].emit('OnlineChanged', [me, ben, maria]);
    hubs[0].emit('ViewersChanged', 'request', 'r1', [me, maria]);
    hubs[0].emit('ViewersChanged', 'request', 'unwatched', [ben]); // not watched: ignored

    expect(presence.online().map((v) => v.userId)).toEqual(['ben', 'maria']);
    expect(presence.viewersOf('request', 'r1')().map((v) => v.userId)).toEqual(['maria']);
    expect(presence.viewersOf('request', 'unwatched')()).toEqual([]);
  });

  it('watch_is_ref_counted', async () => {
    await connect();

    const offA = presence.watch('request', 'r1');
    const offB = presence.watch('request', 'r1');
    await vi.advanceTimersByTimeAsync(0);
    expect(hubs[0].invoked.filter((i) => i.method === 'Watch')).toEqual([{ method: 'Watch', args: ['request', 'r1'] }]);
    expect(presence.viewersOf('request', 'r1')().map((v) => v.userId)).toEqual(['ben']);

    offA();
    offA(); // a second call of the same unsubscribe does nothing
    expect(hubs[0].invokedMethods()).not.toContain('Unwatch');

    offB();
    expect(hubs[0].invoked.filter((i) => i.method === 'Unwatch')).toEqual([{ method: 'Unwatch', args: ['request', 'r1'] }]);
    expect(presence.viewersOf('request', 'r1')()).toEqual([]);
  });

  it('a_watch_made_before_the_connection_is_sent_when_it_opens', async () => {
    presence.watch('order', 'o1');
    await connect();
    await vi.advanceTimersByTimeAsync(0);

    expect(hubs[0].invoked).toContainEqual({ method: 'Watch', args: ['order', 'o1'] });
  });

  it('reconnect_rewatches_every_active_watch', async () => {
    await connect();
    presence.watch('request', 'r1');
    presence.watch('invoice', 'i1');
    await vi.advanceTimersByTimeAsync(0);
    hubs[0].invoked.length = 0;

    hubs[0].dropConnection();
    hubs[0].reconnectOk();
    await vi.advanceTimersByTimeAsync(0);

    expect(hubs[0].invoked).toEqual([
      { method: 'Watch', args: ['request', 'r1'] },
      { method: 'Watch', args: ['invoice', 'i1'] },
    ]);
  });

  it('heartbeat_every_15s_only_while_connected', async () => {
    await connect();
    await vi.advanceTimersByTimeAsync(15_000);
    await vi.advanceTimersByTimeAsync(15_000);
    expect(hubs[0].invokedMethods().filter((m) => m === 'Heartbeat')).toHaveLength(2);

    hubs[0].dropConnection();
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(hubs[0].invokedMethods().filter((m) => m === 'Heartbeat')).toHaveLength(2);

    hubs[0].reconnectOk();
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(15_000);
    expect(hubs[0].invokedMethods().filter((m) => m === 'Heartbeat')).toHaveLength(3);
  });

  it('a_failing_watch_is_not_an_unhandled_error', async () => {
    await connect();
    hubs[0].invokeResult = () => Promise.reject(new Error('HubException: too many watches'));
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    vi.spyOn(console, 'error').mockImplementation(() => undefined);

    presence.watch('request', 'r1');
    await vi.advanceTimersByTimeAsync(0);

    expect(presence.viewersOf('request', 'r1')()).toEqual([]);
  });

  it('logout_clears_presence_and_forgets_watches', async () => {
    await connect();
    hubs[0].emit('OnlineChanged', [me, ben]);
    const off = presence.watch('request', 'r1');
    await vi.advanceTimersByTimeAsync(0);
    expect(presence.online()).toHaveLength(1);

    TestBed.inject(CacheRegistry).clearAll();

    expect(presence.online()).toEqual([]);
    expect(presence.viewersOf('request', 'r1')()).toEqual([]);
    // A component unsubscribing after logout does not send anything.
    hubs[0].invoked.length = 0;
    off();
    expect(hubs[0].invoked).toEqual([]);
    // And the next connection does not re-send the old user's watches.
    hubs[0].dropConnection();
    hubs[0].reconnectOk();
    await vi.advanceTimersByTimeAsync(0);
    expect(hubs[0].invokedMethods()).not.toContain('Watch');
  });
});
