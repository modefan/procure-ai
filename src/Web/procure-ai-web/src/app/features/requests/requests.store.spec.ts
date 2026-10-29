import { TestBed } from '@angular/core/testing';
import { RequestsService } from '../../api/requests/services/requests.service';
import { PagedResultOfRequestDto } from '../../api/requests/models/paged-result-of-request-dto';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsStore } from './requests.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { environment } from '../../../environments/environment';

function dto(n: number, overrides: Partial<RequestDto> = {}): RequestDto {
  return {
    id: `id-${n}`,
    number: `PR-${1000 + n}`,
    title: `Request ${n}`,
    status: 'Draft',
    costCenterCode: 'CC-100',
    supplierId: 's1',
    requesterId: 'tom',
    lines: [],
    submittedAt: null,
    approvedAt: null,
    rejectedAt: null,
    rejectionReason: null,
    cancelledAt: null,
    cancellationReason: null,
    orderId: null,
    orderNumber: null,
    spentInclVat: null,
    spentInclVatDisplay: null,
    cancellationRefusedReason: null,
    totalExclVat: 100,
    totalExclVatDisplay: 'R 100,00',
    vat: 15,
    vatDisplay: 'R 15,00',
    totalInclVat: 115,
    totalInclVatDisplay: 'R 115,00',
    version: 1,
    ...overrides,
  };
}

const page = (items: RequestDto[], p: number, total: number): PagedResultOfRequestDto => ({ items, page: p, pageSize: 25, total });

describe('RequestsStore', () => {
  let listRequests: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    listRequests = vi.fn();
    TestBed.configureTestingModule({ providers: [{ provide: RequestsService, useValue: { listRequests } }] });
  });

  const store = () => TestBed.inject(RequestsStore);

  it('load sets entities and total from the generated client', async () => {
    listRequests.mockResolvedValue(page([dto(1), dto(2), dto(3)], 1, 3));

    await store().load(1);

    expect(listRequests).toHaveBeenCalledWith({ page: 1, pageSize: 25 });
    expect(store().entities().map((r) => r.number)).toEqual(['PR-1001', 'PR-1002', 'PR-1003']);
    expect(store().total()).toBe(3);
    expect(store().page()).toBe(1);
    expect(store().isLoading()).toBe(false);
    expect(store().error()).toBeNull();
  });

  it('shows the loading state while the call runs', async () => {
    let resolve!: (value: PagedResultOfRequestDto) => void;
    listRequests.mockReturnValue(new Promise((r) => (resolve = r)));

    const loading = store().load(1);
    expect(store().isLoading()).toBe(true);
    resolve(page([], 1, 0));
    await loading;

    expect(store().isLoading()).toBe(false);
  });

  it('replaces the entities when another page is loaded and derives the page count', async () => {
    listRequests.mockResolvedValueOnce(page([dto(1)], 1, 61));
    listRequests.mockResolvedValueOnce(page([dto(21)], 3, 61));

    await store().load(1);
    expect(store().pageCount()).toBe(3);
    await store().load(3);

    expect(listRequests).toHaveBeenLastCalledWith({ page: 3, pageSize: 25 });
    expect(store().entities().map((r) => r.number)).toEqual(['PR-1021']);
    expect(store().page()).toBe(3);
  });

  it('keeps the rows it has and reports an error when the call fails', async () => {
    listRequests.mockResolvedValueOnce(page([dto(1)], 1, 1));
    await store().load(1);
    listRequests.mockRejectedValueOnce(new Error('boom'));

    store().clearCache(); // as if the page had gone stale
    await store().load(1);

    expect(store().error()).toBe('The requests could not be loaded.');
    expect(store().isLoading()).toBe(false);
    expect(store().entities()).toHaveLength(1);
  });

  it('ignores the answer of an older call when a newer one was started', async () => {
    let resolveOld!: (value: PagedResultOfRequestDto) => void;
    listRequests.mockReturnValueOnce(new Promise((r) => (resolveOld = r)));
    listRequests.mockResolvedValueOnce(page([dto(21)], 2, 40));

    const old = store().load(1);
    await store().load(2);
    resolveOld(page([dto(1)], 1, 40));
    await old;

    expect(store().page()).toBe(2);
    expect(store().entities().map((r) => r.number)).toEqual(['PR-1021']);
  });

  it('going back to a page it just showed does not call the server again, but a stale page refreshes in the background', async () => {
    vi.useFakeTimers();
    try {
      listRequests.mockResolvedValue(page([dto(1)], 1, 1));
      await store().load(1);
      await store().load(1);
      expect(listRequests).toHaveBeenCalledTimes(1);

      vi.advanceTimersByTime(30_001);
      listRequests.mockResolvedValue(page([dto(1), dto(2)], 1, 2));
      const reload = store().load(1);
      expect(store().isLoading()).toBe(false); // keeps showing what it has
      expect(store().entities()).toHaveLength(1);
      await reload;
      expect(store().entities()).toHaveLength(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('a page other than the one shown is loaded in the foreground, whatever the cache holds', async () => {
    listRequests.mockResolvedValueOnce(page([dto(1)], 1, 41));
    listRequests.mockResolvedValueOnce(page([dto(21)], 2, 41));
    listRequests.mockResolvedValueOnce(page([dto(1)], 1, 41));
    await store().load(1);
    await store().load(2);

    await store().load(1); // page 1 was cached, but the store shows page 2

    expect(store().entities().map((r) => r.number)).toEqual(['PR-1001']);
    expect(listRequests).toHaveBeenCalledTimes(3);
  });

  it('refresh() reloads the shown page now (what a SignalR push will call)', async () => {
    listRequests.mockResolvedValue(page([dto(1)], 1, 1));
    await store().load(1);

    store().revalidateAll();

    await vi.waitFor(() => expect(listRequests).toHaveBeenCalledTimes(2));
  });

  it('an answer arriving after the cache was cleared (logout) is dropped', async () => {
    let resolve!: (value: PagedResultOfRequestDto) => void;
    listRequests.mockReturnValueOnce(new Promise((r) => (resolve = r)));
    const loading = store().load(1);

    store().reset();
    resolve(page([dto(1)], 1, 1));
    await loading;

    expect(store().entities()).toEqual([]);
  });

  it('two_loads_of_the_same_page_before_the_first_answers_make_one_call', async () => {
    let resolve!: (value: PagedResultOfRequestDto) => void;
    listRequests.mockReturnValueOnce(new Promise((r) => (resolve = r)));

    const first = store().load(1);
    const second = store().load(1);
    resolve(page([dto(1)], 1, 1));
    await Promise.all([first, second]);

    expect(listRequests).toHaveBeenCalledTimes(1);
    expect(store().entities().map((r) => r.number)).toEqual(['PR-1001']);
    expect(store().isLoaded()).toBe(true);
  });

  it('list_filters_mine_and_open: the filters go to the server and a change starts at page 1', async () => {
    listRequests.mockResolvedValue(page([dto(1)], 1, 1));
    await store().load(1);

    await store().setFilters({ mine: true });
    expect(listRequests).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, mine: true });

    await store().setFilters({ status: 'open' });
    expect(listRequests).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, mine: true, status: 'Open' });

    await store().setFilters({ mine: false, status: 'closed' });
    expect(listRequests).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, status: 'Closed' });

    await store().setFilters({ status: 'all' });
    expect(listRequests).toHaveBeenLastCalledWith({ page: 1, pageSize: 25 });
    expect(store().mine()).toBe(false);
    expect(store().status()).toBe('all');
  });

  it('paging keeps the filters', async () => {
    listRequests.mockResolvedValue(page([dto(1)], 1, 60));
    await store().setFilters({ mine: true, status: 'open' });

    await store().load(2);

    expect(listRequests).toHaveBeenLastCalledWith({ page: 2, pageSize: 25, mine: true, status: 'Open' });
  });

  it('logout puts the filters back', async () => {
    listRequests.mockResolvedValue(page([], 1, 0));
    await store().setFilters({ mine: true, status: 'closed' });

    store().reset();

    expect(store().mine()).toBe(false);
    expect(store().status()).toBe('all');
  });
});

describe('RequestsStore live refresh', () => {
  it('entity_changed_revalidates_list: a request push refreshes the page on screen quietly; other entities do not', async () => {
    const fake = fakeHubFactory();
    const listRequests = vi.fn().mockResolvedValue(page([dto(1)], 1, 1));
    TestBed.configureTestingModule({
      providers: [
        { provide: RequestsService, useValue: { listRequests } },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
    const live = TestBed.inject(LiveConnection);
    const store = TestBed.inject(RequestsStore);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    await store.load(1);
    listRequests.mockClear();

    fake.hubs[0].emit('EntityChanged', { entity: 'order', id: 'o1', requestId: 'r1', eventType: 'x', occurredAt: 't' });
    expect(listRequests).not.toHaveBeenCalled();

    listRequests.mockResolvedValue(page([dto(1), dto(2)], 1, 2));
    fake.hubs[0].emit('EntityChanged', { entity: 'request', id: 'id-2', requestId: 'id-2', eventType: 'x', occurredAt: 't' });

    expect(store.isLoading()).toBe(false);
    await vi.waitFor(() => expect(store.entities()).toHaveLength(2));
    expect(listRequests).toHaveBeenCalledTimes(1);
    await live.stop();
  });
  it('trailing_reload: a push that comes before Requests saved the new status is followed by a second read (a rejection in another tab)', async () => {
    const fake = fakeHubFactory();
    const listRequests = vi.fn().mockResolvedValue(page([dto(1, { status: 'Submitted' })], 1, 1));
    TestBed.configureTestingModule({
      providers: [
        { provide: RequestsService, useValue: { listRequests } },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
    const live = TestBed.inject(LiveConnection);
    const store = TestBed.inject(RequestsStore);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    await store.load(1);
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      // The push beats the write: the first re-read still returns Submitted. The write lands before the second.
      fake.hubs[0].emit('EntityChanged', { entity: 'request', id: 'id-1', requestId: 'id-1', eventType: 'RequestRejected', occurredAt: 't' });
      await vi.advanceTimersByTimeAsync(0);
      expect(store.entities()[0].status).toBe('Submitted');
      listRequests.mockResolvedValue(page([dto(1, { status: 'Rejected' })], 1, 1));

      await vi.advanceTimersByTimeAsync(environment.cache.trailingReloadMs + 10);

      expect(store.entities()[0].status).toBe('Rejected');
      expect(listRequests).toHaveBeenCalledTimes(3);
    } finally {
      vi.useRealTimers();
    }
    await live.stop();
  });
});
