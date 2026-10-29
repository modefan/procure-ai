import type { Mock } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { signalStore } from '@ngrx/signals';
import { AppConfigService } from '../config/app-config';
import { fakeHubFactory } from '../live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../live/hub-connection';
import { LiveConnection } from '../live/live-connection';
import { PagedResult, withPagedList } from './with-paged-list';

interface Row {
  id: string;
}

const result = (page: number, ids: string[], total = 100): PagedResult<Row> => ({ items: ids.map((id) => ({ id })), page, pageSize: 25, total });

describe('withPagedList', () => {
  let load: Mock<(...args: any[]) => Promise<any>>; // eslint-disable-line @typescript-eslint/no-explicit-any
  let fake: ReturnType<typeof fakeHubFactory>;

  const Store = signalStore(
    { providedIn: 'root' },
    withPagedList<Row, { status: string; mine: boolean }>(() => ({
      entity: 'thing',
      trailing: true,
      initialFilter: { status: 'all', mine: true },
      errorMessage: 'The things could not be loaded.',
      load: (filter, page, pageSize) => load(filter, page, pageSize),
    })),
  );

  beforeEach(() => {
    load = vi.fn(async (_f: unknown, page: number) => result(page, [`r${page}`]));
    fake = fakeHubFactory();
    TestBed.configureTestingModule({ providers: [{ provide: LIVE_HUB_FACTORY, useValue: fake.factory }] });
  });
  const store = () => TestBed.inject(Store);

  it('loads a page with the filter and keeps page, size and total', async () => {
    await store().load(2);

    expect(load).toHaveBeenCalledWith({ status: 'all', mine: true }, 2, 25);
    expect(store().entities()).toEqual([{ id: 'r2' }]);
    expect([store().page(), store().pageSize(), store().total()]).toEqual([2, 25, 100]);
    expect(store().pageCount()).toBe(4);
  });

  it('paged_list_reloads_on_entity_changed: a push about the entity refreshes the page on screen, others do nothing', async () => {
    await store().load(1);
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    load.mockClear();

    fake.hubs[0].emit('EntityChanged', { entity: 'other', id: 'x', requestId: null, eventType: 'x', occurredAt: 't' });
    await Promise.resolve();
    expect(load).not.toHaveBeenCalled();

    fake.hubs[0].emit('EntityChanged', { entity: 'thing', id: 'x', requestId: null, eventType: 'x', occurredAt: 't' });
    await vi.waitFor(() => expect(load).toHaveBeenCalledTimes(1));
    expect(load).toHaveBeenCalledWith({ status: 'all', mine: true }, 1, 25);
    live.stop();
  });

  it('paged_list_filter_change_resets_page: the new filter is sent with page 1 and merged into the old one', async () => {
    await store().load(3);

    await store().setFilter({ status: 'open' });

    expect(load).toHaveBeenLastCalledWith({ status: 'open', mine: true }, 1, 25);
    expect(store().filter()).toEqual({ status: 'open', mine: true }); // merged: the field left out stays
    expect(store().page()).toBe(1);
  });

  it('caches per filter and page: the same page again within the stale time does not load', async () => {
    await store().load(1);
    await store().load(1);

    expect(load).toHaveBeenCalledTimes(1);
  });

  it('a failure sets the error and keeps the rows', async () => {
    await store().load(1);
    load.mockRejectedValue(new Error('down'));

    store().reload();
    await vi.waitFor(() => expect(store().error()).toBe('The things could not be loaded.'));
    expect(store().entities()).toHaveLength(1);
  });

  it('reset puts the filter back', async () => {
    await store().setFilter({ status: 'open' });

    store().reset();

    expect(store().filter()).toEqual({ status: 'all', mine: true });
    expect(store().entities()).toEqual([]);
  });

  it('page_change_during_load_drops_the_old_page: a slow answer for page 1 arriving after page 2 is dropped', async () => {
    let finishFirst!: (r: PagedResult<Row>) => void;
    load.mockImplementationOnce(() => new Promise((r) => (finishFirst = r)));
    const first = store().load(1);

    await store().load(2);
    finishFirst(result(1, ['late']));
    await first;

    expect(store().entities()).toEqual([{ id: 'r2' }]);
    expect(store().page()).toBe(2);
  });

  it('push_triggers_trailing_reload: a push reloads now and once more after cache.trailingReloadMs', async () => {
    await store().load(1);
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    load.mockClear();
    const delay = TestBed.inject(AppConfigService).config().cache.trailingReloadMs;
    vi.useFakeTimers();
    try {
      fake.hubs[0].emit('EntityChanged', { entity: 'thing', id: 'x', requestId: null, eventType: 'x', occurredAt: 't' });
      await vi.advanceTimersByTimeAsync(0);
      expect(load).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(delay + 1);
      expect(load).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
      live.stop();
    }
  });
});
