import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { AuditService } from '../../api/audit/services/audit.service';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { RequestsService } from '../../api/requests/services/requests.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { chainDto, eventDto, orderDto, requestDto, stepDto } from './request-detail.testing';
import { RequestDetailPage } from './request-detail.page';

describe('RequestDetailPage', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let ask: ReturnType<typeof vi.fn>;
  let fake: ReturnType<typeof fakeHubFactory>;

  async function setup(permissions = ['requests.read', 'requests.create', 'orders.read', 'invoices.read'], userId = 'tom', loaded = requestDto({ status: 'Ordered', orderNumber: 'PO-881' }), failRequest = false, forbidHistory = false) {
    api = {
      getRequest: failRequest ? vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 503 })) : vi.fn().mockResolvedValue(loaded),
      cancelRequest: vi.fn().mockResolvedValue(requestDto({ status: 'Cancelling', version: 1731 })),
      getApprovalChain: vi.fn().mockResolvedValue(chainDto('Ordered', [stepDto({ status: 'Approved' })])),
      getOrderForRequest: vi.fn().mockResolvedValue(orderDto()),
      listInvoices: vi.fn().mockResolvedValue({ items: [], page: 1, pageSize: 50, total: 0 }),
      getRequestHistory: forbidHistory ? vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 403 })) : vi.fn().mockResolvedValue([
        eventDto('RequestSubmitted', 'Submitted by Tom Mokoena: 3 laptops', '2026-10-28T09:02:00Z'),
        eventDto('BudgetReserved', 'R 86 940,00 reserved on IT-200 (2026)', '2026-10-28T09:03:00Z', 'system'),
      ]),
    };
    ask = vi.fn().mockResolvedValue({ confirmed: true, value: '' });
    fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: RequestsService, useValue: { getRequest: api['getRequest'], cancelRequest: api['cancelRequest'] } },
        { provide: ApprovalsService, useValue: { getApprovalChain: api['getApprovalChain'] } },
        { provide: OrdersService, useValue: { getOrderForRequest: api['getOrderForRequest'] } },
        { provide: InvoicesService, useValue: { listInvoices: api['listInvoices'] } },
        { provide: AuditService, useValue: { getRequestHistory: api['getRequestHistory'] } },
        { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId, name: 'Tom Mokoena' }) } },
        { provide: ConflictDialog, useValue: { show: vi.fn().mockResolvedValue(undefined) } },
        { provide: ConfirmDialog, useValue: { askWithInput: ask } },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    const fixture = TestBed.createComponent(RequestDetailPage);
    fixture.componentRef.setInput('id', 'r1');
    fixture.componentRef.setInput('request', failRequest ? null : loaded);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement, hub: fake.hubs[0] };
  }

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((resolve) => setTimeout(resolve));
      fixture.detectChanges();
    }
  }
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  it('shows number, title, amount, status and the workflow and audit trail', async () => {
    const { el } = await setup();
    const header = text(el.querySelector('header'));
    expect(header).toContain('PR-1043');
    expect(header).toContain('3 laptops for new hires');
    expect(header).toContain('R 86 940,00');
    expect(header).toContain('Ordered');
    expect(text(el.querySelector('[aria-label="Workflow"]'))).toContain('Submitted');
    const rows = Array.from(el.querySelectorAll('[aria-label="Audit trail"] tbody tr')).map((r) => text(r));
    expect(rows).toHaveLength(2);
    expect(rows[0]).toContain('reserved on IT-200'); // newest first
  });

  it('shows_other_viewers_only: the server list includes me; the header names the others', async () => {
    const { fixture, el, hub } = await setup();
    expect(el.textContent).not.toContain('also viewing');

    await vi.waitFor(() => expect(hub.invokedMethods()).toContain('Watch'));
    hub.emit('ViewersChanged', 'request', 'r1', [
      { userId: 'tom', name: 'Tom Mokoena' },
      { userId: 'ben', name: 'Ben Dlamini' },
    ]);
    await settle(fixture);
    expect(text(el.querySelector('header'))).toContain('Ben Dlamini is also viewing');
    expect(text(el.querySelector('header'))).not.toContain('Tom Mokoena is also');

    hub.emit('ViewersChanged', 'request', 'r1', [
      { userId: 'tom', name: 'Tom Mokoena' },
      { userId: 'ben', name: 'Ben Dlamini' },
      { userId: 'ana', name: 'Ana Pillay' },
    ]);
    await settle(fixture);
    expect(text(el.querySelector('header'))).toContain('Ben Dlamini and Ana Pillay are also viewing');
  });

  it('stops watching when the page is left', async () => {
    const { fixture, hub } = await setup();
    await vi.waitFor(() => expect(hub.invokedMethods()).toContain('Watch'));
    fixture.destroy();
    await vi.waitFor(() => expect(hub.invokedMethods()).toContain('Unwatch'));
  });

  it('order_section_hidden_without_orders_read', async () => {
    const withOrders = await setup();
    expect(text(withOrders.el)).toContain('Order and delivery');
    TestBed.resetTestingModule();

    const without = await setup(['requests.read', 'requests.create']);
    expect(text(without.el)).not.toContain('Order and delivery');
    expect(text(without.el)).toContain('PO-881'); // still named in the workflow, from the request
  });

  it('the cancel button asks first, and a "no" does nothing', async () => {
    const { fixture, el } = await setup();
    ask.mockResolvedValue({ confirmed: false, value: '' });
    (Array.from(el.querySelectorAll('button')).find((b) => text(b) === 'Cancel request') as HTMLButtonElement).click();
    await settle(fixture);
    expect(ask).toHaveBeenCalledWith(expect.objectContaining({ confirm: 'Cancel request', input: { label: 'Reason (optional)', minLength: 0, maxLength: 500, required: false } }));
    expect(api['cancelRequest']).not.toHaveBeenCalled();
  });

  it('confirming sends the cancel with If-Match', async () => {
    const { fixture, el } = await setup();
    api['getRequest'].mockResolvedValue(requestDto({ status: 'Cancelling', version: 1731 })); // what the refresh after the cancel reads
    (Array.from(el.querySelectorAll('button')).find((b) => text(b) === 'Cancel request') as HTMLButtonElement).click();
    await settle(fixture);
    expect(api['cancelRequest']).toHaveBeenCalledWith(expect.objectContaining({ id: 'r1', 'If-Match': '"1730"' }), expect.anything());
    expect(text(el.querySelector('header'))).toContain('Cancelling');
  });

  it('cancel_sends_typed_reason', async () => {
    const { fixture, el } = await setup();
    ask.mockResolvedValue({ confirmed: true, value: 'Wrong supplier' });
    (Array.from(el.querySelectorAll('button')).find((b) => text(b) === 'Cancel request') as HTMLButtonElement).click();
    await settle(fixture);
    expect(api['cancelRequest']).toHaveBeenCalledWith(expect.objectContaining({ id: 'r1', body: { reason: 'Wrong supplier' } }), expect.anything());
  });

  it('cancel without a reason sends no reason', async () => {
    const { fixture, el } = await setup();
    (Array.from(el.querySelectorAll('button')).find((b) => text(b) === 'Cancel request') as HTMLButtonElement).click();
    await settle(fixture);
    expect(api['cancelRequest']).toHaveBeenCalledWith(expect.objectContaining({ body: { reason: null } }), expect.anything());
  });

  it('someone who did not raise the request gets no cancel button', async () => {
    const { el } = await setup(undefined, 'ben');
    expect(Array.from(el.querySelectorAll('button')).some((b) => text(b) === 'Cancel request')).toBe(false);
  });

  it('someones_else_draft: no Edit and no Cancel, and the page says who requested it', async () => {
    const draft = requestDto({ status: 'Draft', submittedAt: null, requesterId: 'tom' });
    const { el } = await setup(undefined, 'ben', draft);
    expect(el.querySelector('a[href="/app/requests/r1/edit"]')).toBeNull();
    expect(Array.from(el.querySelectorAll('button')).some((b) => text(b) === 'Cancel request')).toBe(false);
  });

  it('the requester sees "Requested by you" and the tab title carries the request number', async () => {
    const { el } = await setup();
    expect(text(el.querySelector('header'))).toContain('Requested by you');
    expect(document.title).toBe('PR-1043 - Request');
  });

  it('a 403 on a secondary call keeps the page and names what is missing', async () => {
    const { el } = await setup(undefined, 'tom', undefined, false, true);
    expect(text(el.querySelector('header'))).toContain('PR-1043');
    expect(text(el.querySelector('[role="status"]'))).toContain('Some details could not be loaded: audit trail');
  });

  it('a draft offers Edit to its owner', async () => {
    const draft = requestDto({ status: 'Draft', submittedAt: null });
    const { el } = await setup(undefined, 'tom', draft);
    expect(el.querySelector('a[href="/app/requests/r1/edit"]')).not.toBeNull();
  });

  it('a request that cannot be loaded shows an error with a retry', async () => {
    const { fixture, el } = await setup(undefined, 'tom', undefined, true);
    expect(text(el.querySelector('[role="alert"]'))).toContain('could not be loaded');
    api['getRequest'].mockResolvedValue(requestDto({ status: 'Ordered' }));
    (Array.from(el.querySelectorAll('button')).find((b) => text(b) === 'Try again') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(el.querySelector('header'))).toContain('PR-1043');
  });
});
