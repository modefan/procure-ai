import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { AuditService } from '../../api/audit/services/audit.service';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { RequestsService } from '../../api/requests/services/requests.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { chainDto, eventDto, invoiceDto, orderDto, requestDto, stepDto } from './request-detail.testing';
import { environment } from '../../../environments/environment';
import { RequestDetailStore } from './request-detail.store';

describe('RequestDetailStore', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let conflict: { show: ReturnType<typeof vi.fn> };
  let fake: ReturnType<typeof fakeHubFactory>;

  function setup(permissions: string[] = ['requests.read', 'requests.create', 'orders.read', 'invoices.read'], userId = 'tom') {
    api = {
      getRequest: vi.fn().mockResolvedValue(requestDto({ status: 'Ordered', orderNumber: 'PO-881' })),
      cancelRequest: vi.fn().mockResolvedValue(requestDto({ status: 'Cancelling', version: 1731 })),
      getApprovalChain: vi.fn().mockResolvedValue(chainDto('Ordered', [stepDto({ status: 'Approved' })])),
      getOrderForRequest: vi.fn().mockResolvedValue(orderDto()),
      listInvoices: vi.fn().mockResolvedValue({ items: [invoiceDto()], page: 1, pageSize: 50, total: 1 }),
      getRequestHistory: vi.fn().mockResolvedValue([eventDto('RequestSubmitted', 'Submitted by Tom Mokoena: x')]),
    };
    conflict = { show: vi.fn().mockResolvedValue(undefined) };
    fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        RequestDetailStore,
        { provide: RequestsService, useValue: { getRequest: api['getRequest'], cancelRequest: api['cancelRequest'] } },
        { provide: ApprovalsService, useValue: { getApprovalChain: api['getApprovalChain'] } },
        { provide: OrdersService, useValue: { getOrderForRequest: api['getOrderForRequest'] } },
        { provide: InvoicesService, useValue: { listInvoices: api['listInvoices'] } },
        { provide: AuditService, useValue: { getRequestHistory: api['getRequestHistory'] } },
        { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId, name: 'Tom Mokoena' }) } },
        { provide: ConflictDialog, useValue: conflict },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
    return TestBed.inject(RequestDetailStore);
  }

  it('loads the request, chain, order, invoices of the order and history', async () => {
    const store = setup();
    await store.init('r1', null);

    expect(api['getRequest']).toHaveBeenCalledWith({ id: 'r1' });
    expect(api['getApprovalChain']).toHaveBeenCalledWith({ requestId: 'r1' }, expect.any(HttpContext));
    expect(api['getOrderForRequest']).toHaveBeenCalledWith({ requestId: 'r1' }, expect.any(HttpContext));
    expect(api['listInvoices']).toHaveBeenCalledWith(expect.objectContaining({ orderId: 'o1' }), expect.any(HttpContext));
    expect(api['getRequestHistory']).toHaveBeenCalledWith({ requestId: 'r1' }, expect.any(HttpContext));
    expect(store.request()?.number).toBe('PR-1043');
    expect(store.order()?.number).toBe('PO-881');
    expect(store.invoices()).toHaveLength(1);
    expect(store.history()).toHaveLength(1);
    expect(store.timeline().length).toBeGreaterThan(3);
  });

  it('a request seeded by the resolver is not fetched again', async () => {
    const store = setup();
    await store.init('r1', requestDto({ status: 'Ordered' }));
    expect(api['getRequest']).not.toHaveBeenCalled();
    expect(store.request()?.status).toBe('Ordered');
  });

  it('order_section_hidden_without_orders_read: no order or invoice call, and the section flags are off', async () => {
    const store = setup(['requests.read']);
    await store.init('r1', null);

    expect(api['getOrderForRequest']).not.toHaveBeenCalled();
    expect(api['listInvoices']).not.toHaveBeenCalled();
    expect(store.canSeeOrder()).toBe(false);
    expect(store.canSeeInvoices()).toBe(false);
    expect(store.order()).toBeNull();
  });

  it('orders.read without invoices.read loads the order only', async () => {
    const store = setup(['requests.read', 'orders.read']);
    await store.init('r1', null);
    expect(api['getOrderForRequest']).toHaveBeenCalled();
    expect(api['listInvoices']).not.toHaveBeenCalled();
    expect(store.canSeeInvoices()).toBe(false);
  });

  it('404 for the order means there is none yet, and no invoices are asked for', async () => {
    const store = setup();
    api['getOrderForRequest'].mockRejectedValue(new HttpErrorResponse({ status: 404 }));
    await store.init('r1', null);
    expect(store.order()).toBeNull();
    expect(api['listInvoices']).not.toHaveBeenCalled();
    expect(store.partialFailures()).toEqual([]);
  });

  it('a request that cannot be loaded sets the error and keeps nothing', async () => {
    const store = setup();
    api['getRequest'].mockRejectedValue(new HttpErrorResponse({ status: 503 }));
    await store.init('r1', null);
    expect(store.error()).toBeTruthy();
    expect(store.request()).toBeNull();
  });

  it('a draft has no history: 404 means an empty trail, not a failure', async () => {
    const store = setup();
    api['getRequestHistory'].mockRejectedValue(new HttpErrorResponse({ status: 404 }));
    await store.init('r1', null);
    expect(store.history()).toEqual([]);
    expect(store.partialFailures()).toEqual([]);
  });

  describe('a 403 on a secondary call', () => {
    it('secondary_403_keeps_the_page_and_says_what_is_missing: every secondary call opts out of the forbidden redirect', async () => {
      const store = setup();
      api['getApprovalChain'].mockRejectedValue(new HttpErrorResponse({ status: 403 }));
      api['getRequestHistory'].mockRejectedValue(new HttpErrorResponse({ status: 403 }));
      api['getOrderForRequest'].mockRejectedValue(new HttpErrorResponse({ status: 403 }));
      await store.init('r1', null);

      expect(store.request()).not.toBeNull();
      expect(store.partialFailures()).toEqual(['approval steps', 'order', 'audit trail']);
      const skips = (call: string) => (api[call].mock.calls[0][1] as HttpContext).get(SKIP_FORBIDDEN_REDIRECT);
      expect(skips('getApprovalChain')).toBe(true);
      expect(skips('getOrderForRequest')).toBe(true);
      expect(skips('getRequestHistory')).toBe(true);
    });

    it('the invoices call opts out too', async () => {
      const store = setup();
      await store.init('r1', null);
      expect((api['listInvoices'].mock.calls[0][1] as HttpContext).get(SKIP_FORBIDDEN_REDIRECT)).toBe(true);
    });

    it('when the order fails, only "order" is reported, not the invoices that depend on it', async () => {
      const store = setup();
      api['getOrderForRequest'].mockRejectedValue(new HttpErrorResponse({ status: 500 }));
      await store.init('r1', null);
      expect(store.partialFailures()).toEqual(['order']);
    });
  });

  it('init clears the trailing reload of the request shown before', async () => {
    const store = setup();
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    await store.init('r1', null);
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      fake.hubs[0].emit('EntityChanged', { entity: 'request', id: 'r1', requestId: 'r1', eventType: 'x', occurredAt: 't' });
      await vi.advanceTimersByTimeAsync(0);
      await store.init('r2', null); // the next request opens before the lag has passed
      api['getRequest'].mockClear();
      await vi.advanceTimersByTimeAsync(environment.cache.trailingReloadMs + 10);
      expect(api['getRequest']).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
    await live.stop();
  });

  it('a failing history leaves the page usable and says what is missing', async () => {
    const store = setup();
    api['getRequestHistory'].mockRejectedValue(new HttpErrorResponse({ status: 500 }));
    await store.init('r1', null);
    expect(store.request()).not.toBeNull();
    expect(store.partialFailures()).toEqual(['audit trail']);
  });

  it('live_change_of_this_request_reloads: pushes for this request reload quietly, others do not, bursts are coalesced', async () => {
    const store = setup();
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    await store.init('r1', null);
    api['getRequest'].mockClear();
    const hub = fake.hubs[0];

    hub.emit('EntityChanged', { entity: 'request', id: 'other', requestId: 'other', eventType: 'x', occurredAt: 't' });
    hub.emit('EntityChanged', { entity: 'order', id: 'o9', requestId: 'other', eventType: 'x', occurredAt: 't' });
    expect(api['getRequest']).not.toHaveBeenCalled();

    api['getRequest'].mockResolvedValue(requestDto({ status: 'Completed', version: 1800 }));
    hub.emit('EntityChanged', { entity: 'approval', id: 'r1', requestId: 'r1', eventType: 'x', occurredAt: 't' });
    hub.emit('EntityChanged', { entity: 'order', id: 'o1', requestId: 'r1', eventType: 'x', occurredAt: 't' });
    hub.emit('EntityChanged', { entity: 'invoice', id: 'i1', requestId: 'r1', eventType: 'x', occurredAt: 't' });

    expect(store.isLoading()).toBe(false); // background: the page does not flash a loading state
    await vi.waitFor(() => expect(store.request()?.status).toBe('Completed'));
    await vi.waitFor(() => expect(api['getRequest'].mock.calls.length).toBeLessThanOrEqual(2));
    await live.stop();
  });

  it('a push is followed by one more quiet load, because Audit stores the events a moment later', async () => {
    const store = setup();
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    await store.init('r1', null);
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    api['getRequestHistory'].mockClear();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    try {
      fake.hubs[0].emit('EntityChanged', { entity: 'request', id: 'r1', requestId: 'r1', eventType: 'x', occurredAt: 't' });
      await vi.advanceTimersByTimeAsync(0);
      const afterPush = api['getRequestHistory'].mock.calls.length;
      expect(afterPush).toBe(1);
      await vi.advanceTimersByTimeAsync(environment.cache.trailingReloadMs + 10);
      expect(api['getRequestHistory'].mock.calls.length).toBe(2);
    } finally {
      vi.useRealTimers();
    }
    await live.stop();
  });

  describe('cancel', () => {
    it('sends If-Match with the loaded version and one Idempotency-Key, then shows the new state', async () => {
      const store = setup();
      await store.init('r1', null);

      await store.cancel();

      expect(api['cancelRequest']).toHaveBeenCalledWith(expect.objectContaining({ id: 'r1', 'If-Match': '"1730"', 'Idempotency-Key': expect.any(String) }), expect.any(HttpContext));
      expect(store.request()?.status).toBe('Cancelling');
      expect(store.busy()).toBe(false);
    });

    it('a double click is one call', async () => {
      const store = setup();
      await store.init('r1', null);
      let release!: () => void;
      api['cancelRequest'].mockReturnValue(new Promise((resolve) => (release = () => resolve(requestDto({ status: 'Cancelling', version: 1731 })))));

      const first = store.cancel();
      const second = store.cancel();
      release();
      await Promise.all([first, second]);

      expect(api['cancelRequest']).toHaveBeenCalledTimes(1);
    });

    it('a retry of the same version repeats the key', async () => {
      const store = setup();
      await store.init('r1', null);
      api['cancelRequest'].mockRejectedValueOnce(new HttpErrorResponse({ status: 503 }));

      await store.cancel();
      await store.cancel();

      const keys = api['cancelRequest'].mock.calls.map((c) => c[0]['Idempotency-Key']);
      expect(keys[0]).toBe(keys[1]);
      expect(store.generalError()).toBeNull();
    });

    it('cancel_sends_typed_reason', async () => {
      const store = setup();
      await store.init('r1', null);
      await store.cancel('Wrong supplier');
      expect(api['cancelRequest']).toHaveBeenCalledWith(expect.objectContaining({ body: { reason: 'Wrong supplier' } }), expect.any(HttpContext));
    });

    it('cancel_conflict_shows_dialog_and_reloads', async () => {
      const store = setup();
      await store.init('r1', null);
      api['cancelRequest'].mockRejectedValue(new HttpErrorResponse({ status: 412, error: { detail: 'Someone changed it.' } }));
      api['getRequest'].mockResolvedValue(requestDto({ status: 'Ordered', version: 1999 }));

      await store.cancel();

      expect(conflict.show).toHaveBeenCalledWith('Someone changed it.');
      expect(api['getRequest']).toHaveBeenCalled();
      expect(store.request()?.version).toBe(1999);
      expect(store.busy()).toBe(false);
    });

    it('cancel_403_shows_the_message_and_keeps_the_page (the call opts out of the forbidden redirect)', async () => {
      const store = setup();
      await store.init('r1', null);
      api['cancelRequest'].mockRejectedValue(new HttpErrorResponse({ status: 403, error: { detail: 'Only the requester can cancel this request.' } }));
      await store.cancel();
      expect(store.generalError()).toBe('Only the requester can cancel this request.');
      expect(store.request()).not.toBeNull();
      expect(((api['cancelRequest'].mock.calls[0] as unknown[])[1] as HttpContext).get(SKIP_FORBIDDEN_REDIRECT)).toBe(true);
    });

    it('a refusal (409) is a conflict like a 412: the dialog says why (the server message) and the latest request is loaded', async () => {
      const store = setup();
      await store.init('r1', null);
      api['cancelRequest'].mockRejectedValue(new HttpErrorResponse({ status: 409, error: { detail: 'Goods have been received.' } }));
      api['getRequest'].mockResolvedValue(requestDto({ status: 'Ordered', version: 2001 }));

      await store.cancel();

      expect(conflict.show).toHaveBeenCalledWith('Goods have been received.');
      expect(store.request()?.version).toBe(2001);
      expect(store.generalError()).toBeNull();
    });
  });

  describe('permissions to act', () => {
    it('the requester of a submitted request can cancel and not edit', async () => {
      const mine = setup(undefined, 'tom');
      await mine.init('r1', null);
      expect(mine.canCancel()).toBe(true);
      expect(mine.canEdit()).toBe(false);
    });

    it('someone else who may create requests cannot cancel a request they did not raise', async () => {
      const other = setup(undefined, 'ben');
      await other.init('r1', null);
      expect(other.canCancel()).toBe(false);
    });

    it('someone_elses_draft_offers_no_edit_or_cancel', async () => {
      const store = setup(undefined, 'ben');
      api['getRequest'].mockResolvedValue(requestDto({ status: 'Draft', submittedAt: null, requesterId: 'tom' }));
      api['getApprovalChain'].mockRejectedValue(new HttpErrorResponse({ status: 404 }));
      api['getRequestHistory'].mockRejectedValue(new HttpErrorResponse({ status: 404 }));
      await store.init('r1', null);
      expect(store.canEdit()).toBe(false);
      expect(store.canCancel()).toBe(false);
    });

    it('my own draft can be edited and cancelled', async () => {
      const store = setup(undefined, 'tom');
      api['getRequest'].mockResolvedValue(requestDto({ status: 'Draft', submittedAt: null }));
      api['getApprovalChain'].mockRejectedValue(new HttpErrorResponse({ status: 404 }));
      api['getRequestHistory'].mockRejectedValue(new HttpErrorResponse({ status: 404 }));
      await store.init('r1', null);
      expect(store.canEdit()).toBe(true);
      expect(store.canCancel()).toBe(true);
    });

    it('requested_by: "you" for my own, the name Audit knows for others, nothing when nobody knows it', async () => {
      const mine = setup(undefined, 'tom');
      await mine.init('r1', null);
      expect(mine.requestedBy()).toBe('you');
      TestBed.resetTestingModule();

      const other = setup(undefined, 'ben');
      api['getRequestHistory'].mockResolvedValue([eventDto('RequestSubmitted', 'x', undefined, 'tom', { actorName: 'Tom Mokoena' })]);
      await other.init('r1', null);
      expect(other.requestedBy()).toBe('Tom Mokoena');
      TestBed.resetTestingModule();

      const draft = setup(undefined, 'ben');
      api['getRequest'].mockResolvedValue(requestDto({ status: 'Draft', submittedAt: null }));
      api['getRequestHistory'].mockRejectedValue(new HttpErrorResponse({ status: 404 }));
      await draft.init('r1', null);
      expect(draft.requestedBy()).toBeNull();
    });

    it('no cancel once goods were received, when completed, or without requests.create', async () => {
      const received = setup();
      api['getOrderForRequest'].mockResolvedValue(orderDto({ status: 'PartiallyReceived', receipts: [{ id: 'g', receivedAt: 't', receivedBy: 'u', deliveryNote: null, lines: [] }] }));
      await received.init('r1', null);
      expect(received.canCancel()).toBe(false);
    });

    it('cancel_hidden_without_orders_read', async () => {
      // Without orders.read the page cannot see whether goods were received, so it cannot promise the cancel will work.
      const store = setup(['requests.read', 'requests.create']);
      await store.init('r1', null);
      expect(store.canCancel()).toBe(false);
      TestBed.resetTestingModule();

      const draft = setup(['requests.read', 'requests.create']);
      api['getRequest'].mockResolvedValue(requestDto({ status: 'Draft', submittedAt: null }));
      api['getApprovalChain'].mockRejectedValue(new HttpErrorResponse({ status: 404 }));
      api['getRequestHistory'].mockRejectedValue(new HttpErrorResponse({ status: 404 }));
      await draft.init('r1', null);
      expect(draft.canCancel()).toBe(true); // a draft has no order to look at
    });

    it('no cancel without requests.create', async () => {
      const store = setup(['requests.read']);
      await store.init('r1', null);
      expect(store.canCancel()).toBe(false);
    });
  });
});
