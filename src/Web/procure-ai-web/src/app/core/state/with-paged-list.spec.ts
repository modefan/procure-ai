import type { Mock } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { signalStore } from '@ngrx/signals';
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
    withPagedList<Row, { status: string }>(() => ({
      entity: 'thing',
      initialFilter: { status: 'all' },
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

    expect(load).toHaveBeenCalledWith({ status: 'all' }, 2, 25);
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
    expect(load).toHaveBeenCalledWith({ status: 'all' }, 1, 25);
    live.stop();
  });

  it('paged_list_filter_change_resets_page: the new filter is sent with page 1 and merged into the old one', async () => {
    await store().load(3);

    await store().setFilter({ status: 'open' });

    expect(load).toHaveBeenLastCalledWith({ status: 'open' }, 1, 25);
    expect(store().filter()).toEqual({ status: 'open' });
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

    expect(store().filter()).toEqual({ status: 'all' });
    expect(store().entities()).toEqual([]);
  });
});
