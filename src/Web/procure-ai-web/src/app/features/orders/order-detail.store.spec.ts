import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { invoiceDto } from '../requests/request-detail.testing';
import { OrderDetailStore } from './order-detail.store';
import { order, orderLine } from './orders.testing';

describe('OrderDetailStore', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let conflict: { show: ReturnType<typeof vi.fn> };
  let fake: ReturnType<typeof fakeHubFactory>;

  function setup(permissions = ['orders.read', 'orders.receive', 'invoices.read', 'requests.read']) {
    api = {
      getOrder: vi.fn().mockResolvedValue(order()),
      receiveGoods: vi.fn().mockResolvedValue(order({ status: 'PartiallyReceived', version: 2 })),
      closeOrderShort: vi.fn().mockResolvedValue(order({ status: 'ClosedShort', version: 3 })),
      listInvoices: vi.fn().mockResolvedValue({ items: [invoiceDto()], page: 1, pageSize: 50, total: 1 }),
    };
    conflict = { show: vi.fn().mockResolvedValue(undefined) };
    fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        OrderDetailStore,
        { provide: OrdersService, useValue: { getOrder: api['getOrder'], receiveGoods: api['receiveGoods'], closeOrderShort: api['closeOrderShort'] } },
        { provide: InvoicesService, useValue: { listInvoices: api['listInvoices'] } },
        { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId: 'tom', name: 'Tom' }) } },
        { provide: ConflictDialog, useValue: conflict },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
    return TestBed.inject(OrderDetailStore);
  }
  const push = (entity: string, id: string, requestId = 'r1') => fake.hubs[0].emit('EntityChanged', { entity, id, requestId, eventType: 'x', occurredAt: 't' });
  async function connect() {
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    return live;
  }

  it('loads the order and its invoices (one call, no per-row calls)', async () => {
    const store = setup();
    await store.init('o1', null);
    expect(api['getOrder']).toHaveBeenCalledWith({ id: 'o1' }, expect.any(HttpContext));
    expect(api['listInvoices']).toHaveBeenCalledTimes(1);
    expect(api['listInvoices']).toHaveBeenCalledWith(expect.objectContaining({ orderId: 'o1' }), expect.any(HttpContext));
    expect(store.order()?.number).toBe('PO-881');
    expect(store.invoices()).toHaveLength(1);
  });

  it('an order seeded by the resolver is not fetched again, and no invoice call without invoices.read', async () => {
    const store = setup(['orders.read']);
    await store.init('o1', order());
    expect(api['getOrder']).not.toHaveBeenCalled();
    expect(api['listInvoices']).not.toHaveBeenCalled();
  });

  it('receive sends only positive lines, If-Match from the loaded version and an Idempotency-Key', async () => {
    const store = setup();
    await store.init('o1', order({ version: 7 }));
    const ok = await store.receive({ deliveryNote: 'DN-1', lines: [{ lineNo: 1, quantity: 2 }, { lineNo: 2, quantity: 0 }] });
    expect(ok).toBe(true);
    const params = api['receiveGoods'].mock.calls[0][0];
    expect(params).toMatchObject({ id: 'o1', 'If-Match': '"7"', body: { deliveryNote: 'DN-1', lines: [{ lineNo: 1, quantity: 2 }] } });
    expect(params['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(store.order()?.version).toBe(2);
  });

  it('a double click is one call; a retry of the same receipt repeats the key', async () => {
    const store = setup();
    await store.init('o1', order());
    let finish!: (o: unknown) => void;
    api['receiveGoods'].mockReturnValueOnce(new Promise((r) => (finish = r)));
    const body = { deliveryNote: null, lines: [{ lineNo: 1, quantity: 1 }] };
    const first = store.receive(body);
    const second = store.receive(body);
    expect(store.busy()).toBe('receiving');
    finish(order({ version: 2 }));
    await Promise.all([first, second]);
    expect(api['receiveGoods']).toHaveBeenCalledTimes(1);
    expect(store.busy()).toBe('idle');

    await store.init('o1', order({ version: 9 }));
    api['receiveGoods'].mockRejectedValue(new HttpErrorResponse({ status: 503 }));
    await store.receive(body);
    await store.receive(body);
    const keys = api['receiveGoods'].mock.calls.slice(1).map((c) => c[0]['Idempotency-Key']);
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it('a 412 opens the conflict dialog and reloads the latest order', async () => {
    const store = setup();
    await store.init('o1', order());
    api['receiveGoods'].mockRejectedValue(new HttpErrorResponse({ status: 412 }));
    api['getOrder'].mockResolvedValue(order({ version: 5 }));
    const ok = await store.receive({ deliveryNote: null, lines: [{ lineNo: 1, quantity: 1 }] });
    expect(ok).toBe(false);
    expect(conflict.show).toHaveBeenCalled();
    expect(store.order()?.version).toBe(5);
  });

  it('a 422 shows the server message; field errors are keyed by order line number', async () => {
    const store = setup();
    await store.init('o1', order());
    api['receiveGoods'].mockRejectedValueOnce(new HttpErrorResponse({ status: 422, error: { title: 'Nope', detail: 'The order is cancelled.' } }));
    await store.receive({ deliveryNote: null, lines: [{ lineNo: 1, quantity: 1 }] });
    expect(store.generalError()).toBe('The order is cancelled.');

    api['receiveGoods'].mockRejectedValueOnce(new HttpErrorResponse({ status: 400, error: { errors: { 'lines[1].quantity': ['Too many.'] } } }));
    await store.receive({ deliveryNote: null, lines: [{ lineNo: 1, quantity: 1 }, { lineNo: 2, quantity: 5 }] });
    expect(store.fieldErrors()).toEqual({ 'line.2': ['Too many.'] });
    expect(store.generalError()).toBeNull();
  });

  it('close short sends If-Match, a key and a reason, and shows the updated order', async () => {
    const store = setup();
    await store.init('o1', order({ status: 'PartiallyReceived', version: 4 }));
    expect(await store.closeShort()).toBe(true);
    expect(api['closeOrderShort']).toHaveBeenCalledWith(
      expect.objectContaining({ id: 'o1', 'If-Match': '"4"', body: { reason: expect.stringMatching(/\S/) } }),
      expect.any(HttpContext),
    );
    expect(store.order()?.status).toBe('ClosedShort');
  });

  it('entity_changed_revalidates: a push for this order or its request reloads quietly; others do nothing', async () => {
    const store = setup();
    const live = await connect();
    await store.init('o1', order());
    api['getOrder'].mockClear();
    api['getOrder'].mockResolvedValue(order({ version: 2, lines: [orderLine(1, 2, 1), orderLine(2, 1)] }));

    push('order', 'other', 'other');
    push('invoice', 'x', 'other');
    await new Promise((r) => setTimeout(r, 20));
    expect(api['getOrder']).not.toHaveBeenCalled();

    push('order', 'o1');
    await vi.waitFor(() => expect(store.order()?.version).toBe(2));
    expect(store.isLoading()).toBe(false);

    api['listInvoices'].mockClear();
    push('invoice', 'i9', 'r1');
    await vi.waitFor(() => expect(api['listInvoices']).toHaveBeenCalled());
    await live.stop();
  });

  it('a reconnect reloads', async () => {
    const store = setup();
    const live = await connect();
    await store.init('o1', order());
    api['getOrder'].mockClear();
    fake.hubs[0].dropConnection();
    fake.hubs[0].reconnectOk();
    await vi.waitFor(() => expect(api['getOrder']).toHaveBeenCalled());
    await live.stop();
  });

  it('clearCache forgets the order', async () => {
    const store = setup();
    await store.init('o1', order());
    store.clearCache();
    expect(store.order()).toBeNull();
  });
});
