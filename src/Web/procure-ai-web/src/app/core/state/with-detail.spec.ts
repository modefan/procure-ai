import type { Mock } from 'vitest';
import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { signalStore } from '@ngrx/signals';
import { fakeHubFactory } from '../live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../live/hub-connection';
import { LiveConnection } from '../live/live-connection';
import { ConflictDialog } from '../ui/conflict-dialog';
import { CacheRegistry } from './cache-registry';
import { withDetail } from './with-detail';

interface Thing {
  id: string;
  version: number;
}

describe('withDetail', () => {
  let load: Mock<(...args: any[]) => Promise<any>>; // eslint-disable-line @typescript-eslint/no-explicit-any
  let conflict: { show: ReturnType<typeof vi.fn> };
  let fake: ReturnType<typeof fakeHubFactory>;

  const Store = signalStore(
    { providedIn: 'root' },
    withDetail<Thing>(() => ({
      entity: 'thing',
      errorMessage: 'The thing could not be loaded.',
      isRelevant: (change, id) => change.id === id,
      load: async (id) => {
        const value = (await load(id)) as Thing;
        return { value, etag: `"${value.version}"` };
      },
    })),
  );

  beforeEach(() => {
    load = vi.fn(async (id: string) => ({ id, version: 1 }));
    conflict = { show: vi.fn().mockResolvedValue(undefined) };
    fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        { provide: ConflictDialog, useValue: conflict },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
  });
  const store = () => TestBed.inject(Store);

  it('open loads the value and its etag', async () => {
    await store().open('t1');

    expect(store().value()).toEqual({ id: 't1', version: 1 });
    expect(store().etag()).toBe('"1"');
    expect(store().isLoaded()).toBe(true);
  });

  it('a failed first load sets the error', async () => {
    load.mockRejectedValue(new Error('down'));

    await store().open('t1');

    expect(store().error()).toBe('The thing could not be loaded.');
  });

  it('registers with the cache registry, so logout clears it', async () => {
    await store().open('t1');

    TestBed.inject(CacheRegistry).clearAll();

    expect(store().value()).toBeNull();
    expect(store().id()).toBeNull();
  });

  it('a push about the open thing reloads quietly, others do nothing', async () => {
    await store().open('t1');
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    load.mockClear();

    fake.hubs[0].emit('EntityChanged', { entity: 'thing', id: 'other', requestId: null, eventType: 'x', occurredAt: 't' });
    await Promise.resolve();
    expect(load).not.toHaveBeenCalled();
    fake.hubs[0].emit('EntityChanged', { entity: 'thing', id: 't1', requestId: null, eventType: 'x', occurredAt: 't' });
    await vi.waitFor(() => expect(load).toHaveBeenCalledTimes(1));
    live.stop();
  });

  const problem = (status: number) => new HttpErrorResponse({ status, error: { title: 'x' } });
  const options = { what: 'saving', failureMessage: 'Saving did not work.', conflictMessage: 'Someone else changed it.' };

  it('detail_run_write_conflict_shows_dialog_and_reloads', async () => {
    await store().open('t1');
    load.mockClear();
    load.mockResolvedValue({ id: 't1', version: 2 });

    const agreed = await store().runWrite(() => Promise.reject(problem(412)), options);

    expect(agreed).toBe(false);
    expect(conflict.show).toHaveBeenCalledWith('Someone else changed it.');
    expect(load).toHaveBeenCalledTimes(1);
    expect(store().value()).toEqual({ id: 't1', version: 2 });
    expect(store().busyKind()).toBeNull();
  });

  it('a 409 is a conflict like a 412', async () => {
    await store().open('t1');

    await store().runWrite(() => Promise.reject(problem(409)), options);

    expect(conflict.show).toHaveBeenCalledTimes(1);
  });

  it('detail_run_write_one_key_per_call: the write gets the etag and one new key per call; the same key parts repeat the key', async () => {
    await store().open('t1');
    const fn = vi.fn().mockResolvedValue(undefined);

    await store().runWrite(fn, options);
    await store().runWrite(fn, options);
    await store().runWrite(fn, { ...options, keyParts: ['same'] });
    await store().runWrite(fn, { ...options, keyParts: ['same'] });

    const keys = fn.mock.calls.map((c) => c[1] as string);
    expect(fn.mock.calls[0][0]).toBe('"1"');
    expect(keys[0]).not.toBe(keys[1]);
    expect(keys[2]).toBe(keys[3]);
    expect(keys[2]).not.toBe(keys[0]);
  });

  it('a double click shares the running write, a different kind meanwhile is refused, and busyKind is set while it runs', async () => {
    await store().open('t1');
    let finish!: () => void;
    const fn = vi.fn(() => new Promise<void>((r) => (finish = r)));

    const first = store().runWrite(fn, { ...options, kind: 'a' });
    const second = store().runWrite(fn, { ...options, kind: 'a' });
    const other = await store().runWrite(fn, { ...options, kind: 'b' });
    expect(store().busyKind()).toBe('a');
    finish();

    expect(other).toBe(false);
    expect(await first).toBe(true);
    expect(await second).toBe(true);
    expect(fn).toHaveBeenCalledTimes(1);
    expect(store().busyKind()).toBeNull();
  });

  it('another failure shows the server message or the default and does not open the dialog', async () => {
    await store().open('t1');

    await store().runWrite(() => Promise.reject(new Error('boom')), options);

    expect(store().generalError()).toBe('Saving did not work.');
    expect(conflict.show).not.toHaveBeenCalled();
  });

  it('apply shows the answer at once', async () => {
    await store().open('t1');

    await store().runWrite(() => Promise.resolve({ id: 't1', version: 5 }), { ...options, apply: (r: Thing) => ({ value: r, etag: `"${r.version}"` }) });

    expect(store().value()).toEqual({ id: 't1', version: 5 });
    expect(store().etag()).toBe('"5"');
  });
});
