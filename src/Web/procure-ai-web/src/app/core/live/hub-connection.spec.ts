import { FakeHub } from './fake-hub.testing';
import { LazyLiveHub } from './hub-connection';

/**
 * The SignalR client (about 64 kB) is not part of the initial bundle: `LazyLiveHub` loads it when the connection starts, after
 * sign-in. Handlers registered before that are attached to the real hub once it exists.
 */
describe('LazyLiveHub', () => {
  it('loads nothing until start, then attaches every handler registered before it', async () => {
    const real = new FakeHub();
    const build = vi.fn().mockResolvedValue(real);
    const hub = new LazyLiveHub(build);
    const pushes: unknown[] = [];
    const events: string[] = [];
    hub.on('EntityChanged', (change: unknown) => pushes.push(change));
    hub.onreconnecting(() => events.push('reconnecting'));
    hub.onreconnected(() => events.push('reconnected'));
    hub.onclose(() => events.push('closed'));
    expect(build).not.toHaveBeenCalled();

    await hub.start();

    expect(build).toHaveBeenCalledOnce();
    real.emit('EntityChanged', { id: 'PR-1' });
    real.dropConnection();
    real.reconnectOk();
    real.giveUp();
    expect(pushes).toEqual([{ id: 'PR-1' }]);
    expect(events).toEqual(['reconnecting', 'reconnected', 'closed']);
  });

  it('a handler added after start goes straight to the real hub; invoke uses it', async () => {
    const real = new FakeHub();
    const hub = new LazyLiveHub(() => Promise.resolve(real));
    await hub.start();
    const seen: unknown[] = [];
    hub.on('ViewersChanged', (...args: unknown[]) => seen.push(args));
    real.emit('ViewersChanged', 'request', 'r1', []);
    await hub.invoke('Watch', 'request', 'r1');
    expect(seen).toEqual([['request', 'r1', []]]);
    expect(real.invoked).toEqual([{ method: 'Watch', args: ['request', 'r1'] }]);
  });

  it('stop before start loads nothing; stop after start stops the real hub', async () => {
    const build = vi.fn().mockResolvedValue(new FakeHub());
    await new LazyLiveHub(build).stop();
    expect(build).not.toHaveBeenCalled();

    const real = new FakeHub();
    const hub = new LazyLiveHub(() => Promise.resolve(real));
    await hub.start();
    await hub.stop();
    expect(real.stopped).toBe(true);
  });

  it('a failed load rejects start, so the connection goes offline and retries with a new hub', async () => {
    const hub = new LazyLiveHub(() => Promise.reject(new Error('chunk load failed')));
    await expect(hub.start()).rejects.toThrow('chunk load failed');
    await expect(hub.stop()).resolves.toBeUndefined();
  });
});
