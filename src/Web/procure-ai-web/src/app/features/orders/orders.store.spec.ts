import { TestBed } from '@angular/core/testing';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { environment } from '../../../environments/environment';
import { OrdersStore } from './orders.store';
import { order, ordersPage } from './orders.testing';

describe('OrdersStore', () => {
  let listOrders: ReturnType<typeof vi.fn>;
  let fake: ReturnType<typeof fakeHubFactory>;

  beforeEach(() => {
    listOrders = vi.fn().mockResolvedValue(ordersPage([order()]));
    fake = fakeHubFactory();
    TestBed.configureTestingModule({ providers: [{ provide: OrdersService, useValue: { listOrders } }, { provide: LIVE_HUB_FACTORY, useValue: fake.factory }] });
  });
  const store = () => TestBed.inject(OrdersStore);

  it('loads a page through the generated client', async () => {
    await store().load(1);
    expect(listOrders).toHaveBeenCalledWith({ page: 1, pageSize: 25 });
    expect(store().entities()).toHaveLength(1);
    expect(store().total()).toBe(1);
  });

  it('a status filter is sent and shows page 1 of the result', async () => {
    await store().setStatus('PartiallyReceived');
    expect(listOrders).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, status: 'PartiallyReceived' });
    await store().setStatus('all');
    expect(listOrders).toHaveBeenLastCalledWith({ page: 1, pageSize: 25 });
  });

  it('a failure sets the error and keeps the rows', async () => {
    await store().load(1);
    listOrders.mockRejectedValue(new Error('down'));
    store().invalidate('list');
    await store().load(1);
    await vi.waitFor(() => expect(store().error()).toBe('The orders could not be loaded.'));
    expect(store().entities()).toHaveLength(1);
  });

  it('entity_changed_revalidates: an order push refreshes the page on screen in the background', async () => {
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    TestBed.inject(OrdersStore);
    await store().load(1);
    listOrders.mockResolvedValue(ordersPage([order(), order({ id: 'o2', number: 'PO-882' })]));
    fake.hubs[0].emit('EntityChanged', { entity: 'order', id: 'o2', requestId: 'r2', eventType: 'OrderConfirmed', occurredAt: '2026-10-28T10:00:00Z' });
    await vi.waitFor(() => expect(store().entities()).toHaveLength(2));
    expect(store().isLoading()).toBe(false);
    live.stop();
  });

  it('trailing_reload: an order push is followed by a second read, for what Purchasing saves from another event', async () => {
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    TestBed.inject(OrdersStore);
    await store().load(1);
    listOrders.mockClear();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      fake.hubs[0].emit('EntityChanged', { entity: 'order', id: 'o1', requestId: 'r1', eventType: 'PaymentReleased', occurredAt: 't' });
      await vi.advanceTimersByTimeAsync(0);
      expect(listOrders).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(environment.cache.trailingReloadMs + 10);

      expect(listOrders).toHaveBeenCalledTimes(2);
    } finally {
      vi.useRealTimers();
    }
    await live.stop();
  });
});
