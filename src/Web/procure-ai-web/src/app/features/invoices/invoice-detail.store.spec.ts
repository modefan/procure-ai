import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { AuthStore } from '../../core/auth/auth.store';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { InvoiceDetailStore } from './invoice-detail.store';
import { mismatched, payment } from './invoices.testing';

describe('InvoiceDetailStore', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let conflict: { show: ReturnType<typeof vi.fn> };
  let fake: ReturnType<typeof fakeHubFactory>;

  function setup(permissions = ['invoices.read', 'invoices.release-payment', 'orders.read']) {
    api = {
      getInvoice: vi.fn().mockResolvedValue(mismatched()),
      releasePayment: vi.fn().mockResolvedValue(mismatched({ status: 'PartiallyPaid', version: 2, payments: [payment()] })),
      holdInvoice: vi.fn().mockResolvedValue(mismatched({ onHold: true, holdReason: 'Bank details changed', version: 2 })),
      releaseInvoiceHold: vi.fn().mockResolvedValue(mismatched({ version: 3 })),
    };
    conflict = { show: vi.fn().mockResolvedValue(undefined) };
    fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        InvoiceDetailStore,
        { provide: InvoicesService, useValue: api },
        { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId: 'thandi', name: 'Thandi' }) } },
        { provide: ConflictDialog, useValue: conflict },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
    return TestBed.inject(InvoiceDetailStore);
  }
  const push = (entity: string, id: string, requestId = 'r1') => fake.hubs[0].emit('EntityChanged', { entity, id, requestId, eventType: 'x', occurredAt: 't' });
  async function connect() {
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    return live;
  }

  it('loads the invoice unless the resolver already did', async () => {
    const store = setup();
    await store.init('i1', null);
    expect(api['getInvoice']).toHaveBeenCalledWith({ id: 'i1' }, expect.any(HttpContext));
    expect(store.invoice()?.supplierInvoiceNumber).toBe('INV-5521');
    api['getInvoice'].mockClear();
    await store.init('i1', mismatched());
    expect(api['getInvoice']).not.toHaveBeenCalled();
  });

  it('pay_sends_if_match_and_one_idempotency_key: a double click is one call, a retry repeats the key', async () => {
    const store = setup();
    await store.init('i1', mismatched({ version: 7 }));
    let finish!: (o: unknown) => void;
    api['releasePayment'].mockReturnValueOnce(new Promise((r) => (finish = r)));
    const first = store.pay(null);
    const second = store.pay(null);
    expect(store.busy()).toBe('paying');
    finish(mismatched({ status: 'PartiallyPaid', version: 8 }));
    expect(await Promise.all([first, second])).toEqual([true, true]);
    expect(api['releasePayment']).toHaveBeenCalledTimes(1);
    const params = api['releasePayment'].mock.calls[0][0];
    expect(params).toMatchObject({ id: 'i1', 'If-Match': '"7"', body: { lines: null } });
    expect(params['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(store.invoice()?.version).toBe(8);
    expect(store.busy()).toBe('idle');

    await store.init('i1', mismatched({ version: 9 }));
    api['releasePayment'].mockRejectedValue(new HttpErrorResponse({ status: 503 }));
    await store.pay(null);
    await store.pay(null);
    const keys = api['releasePayment'].mock.calls.slice(1).map((c) => c[0]['Idempotency-Key']);
    expect(keys).toHaveLength(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it('pays chosen quantities only', async () => {
    const store = setup();
    await store.init('i1', mismatched());
    await store.pay([{ lineNo: 1, quantity: 1 }]);
    expect(api['releasePayment'].mock.calls[0][0].body).toEqual({ lines: [{ lineNo: 1, quantity: 1 }] });
  });

  it('conflict_412_reloads: the dialog opens and the latest invoice is loaded', async () => {
    const store = setup();
    await store.init('i1', mismatched());
    api['releasePayment'].mockRejectedValue(new HttpErrorResponse({ status: 412 }));
    api['getInvoice'].mockResolvedValue(mismatched({ version: 5, status: 'PartiallyPaid' }));
    expect(await store.pay(null)).toBe(false);
    expect(conflict.show).toHaveBeenCalledWith(expect.stringContaining('invoice'));
    expect(store.invoice()?.version).toBe(5);
    expect(store.generalError()).toBeNull();
    expect(store.busy()).toBe('idle');
  });

  it('a separation-of-duties 403 shows its own message, without the forbidden redirect', async () => {
    const store = setup();
    await store.init('i1', mismatched());
    api['releasePayment'].mockRejectedValueOnce(
      new HttpErrorResponse({ status: 403, error: { title: 'Forbidden', detail: 'You raised this request, so you cannot release its payment.' } }),
    );
    expect(await store.pay(null)).toBe(false);
    expect(store.generalError()).toBe('You raised this request, so you cannot release its payment.');
    const context = api['releasePayment'].mock.calls[0][1] as HttpContext;
    expect(context.get(SKIP_FORBIDDEN_REDIRECT)).toBe(true);
  });

  it('a 422 shows the server message', async () => {
    const store = setup();
    await store.init('i1', mismatched());
    api['releasePayment'].mockRejectedValueOnce(new HttpErrorResponse({ status: 422, error: { detail: 'Nothing is payable on this invoice yet.' } }));
    await store.pay(null);
    expect(store.generalError()).toBe('Nothing is payable on this invoice yet.');
  });

  it('hold and release hold send If-Match and a key', async () => {
    const store = setup();
    await store.init('i1', mismatched({ version: 4 }));
    expect(await store.hold('Bank details changed')).toBe(true);
    expect(api['holdInvoice'].mock.calls[0][0]).toMatchObject({ id: 'i1', 'If-Match': '"4"', body: { reason: 'Bank details changed' } });
    expect(api['holdInvoice'].mock.calls[0][0]['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(store.invoice()?.onHold).toBe(true);
    expect(await store.releaseHold()).toBe(true);
    expect(api['releaseInvoiceHold'].mock.calls[0][0]).toMatchObject({ id: 'i1', 'If-Match': '"2"' });
    expect(store.invoice()?.onHold).toBe(false);
  });

  it('a different action during a running write is refused', async () => {
    const store = setup();
    await store.init('i1', mismatched());
    let finish!: (o: unknown) => void;
    api['releasePayment'].mockReturnValueOnce(new Promise((r) => (finish = r)));
    const paying = store.pay(null);
    expect(await store.hold('Check the bank details')).toBe(false);
    expect(api['holdInvoice']).not.toHaveBeenCalled();
    finish(mismatched({ version: 2 }));
    expect(await paying).toBe(true);
  });

  it('knows what the caller may do', async () => {
    const finance = setup();
    await finance.init('i1', mismatched());
    expect(finance.mayRelease()).toBe(true);
    TestBed.resetTestingModule();
    const requester = setup(['invoices.read']);
    await requester.init('i1', mismatched());
    expect(requester.mayRelease()).toBe(false);
    expect(requester.canSeeOrder()).toBe(false);
  });

  it('entity_changed_revalidates: a push for this invoice or its request reloads quietly; others do nothing', async () => {
    const store = setup();
    const live = await connect();
    await store.init('i1', mismatched());
    api['getInvoice'].mockClear();
    api['getInvoice'].mockResolvedValue(mismatched({ version: 2 }));

    push('invoice', 'other', 'other');
    await new Promise((r) => setTimeout(r, 20));
    expect(api['getInvoice']).not.toHaveBeenCalled();

    push('invoice', 'i1');
    await vi.waitFor(() => expect(store.invoice()?.version).toBe(2));
    expect(store.isLoading()).toBe(false);

    api['getInvoice'].mockClear();
    push('order', 'o1', 'r1');
    await vi.waitFor(() => expect(api['getInvoice']).toHaveBeenCalled());
    await live.stop();
  });

  it('a reconnect reloads, and clearCache forgets the invoice', async () => {
    const store = setup();
    const live = await connect();
    await store.init('i1', mismatched());
    api['getInvoice'].mockClear();
    fake.hubs[0].dropConnection();
    fake.hubs[0].reconnectOk();
    await vi.waitFor(() => expect(api['getInvoice']).toHaveBeenCalled());
    store.clearCache();
    expect(store.invoice()).toBeNull();
    await live.stop();
  });
});
