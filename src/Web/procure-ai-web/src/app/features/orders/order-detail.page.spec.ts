import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { SuppliersService } from '../../api/suppliers/services/suppliers.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { invoiceDto } from '../requests/request-detail.testing';
import { OrderDetailPage } from './order-detail.page';
import { order, orderLine } from './orders.testing';

describe('OrderDetailPage', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let ask: ReturnType<typeof vi.fn>;
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const receipt = { id: 'g1', deliveryNote: 'DN-5', receivedAt: '2026-10-29T08:00:00Z', receivedBy: 'u1', lines: [{ lineNo: 1, quantity: 2 }] };

  async function setup(loaded = order(), permissions = ['orders.read', 'orders.receive', 'invoices.read', 'requests.read']) {
    api = {
      getOrder: vi.fn().mockResolvedValue(loaded),
      receiveGoods: vi.fn().mockResolvedValue(order({ status: 'PartiallyReceived', version: 2, lines: [orderLine(1, 2, 1), orderLine(2, 1)], receipts: [receipt] })),
      closeOrderShort: vi.fn().mockResolvedValue(order({ status: 'ClosedShort', version: 3 })),
      listInvoices: vi.fn().mockResolvedValue({ items: [invoiceDto({ lines: [{ lineNo: 1, quantity: 2, paid: 1 }] as never })], page: 1, pageSize: 50, total: 1 }),
      listSuppliers: vi.fn().mockResolvedValue({ items: [{ code: 'CH', name: 'Contoso Hardware', active: true, itemCount: 4 }], page: 1, pageSize: 100, total: 1 }),
    };
    ask = vi.fn().mockResolvedValue(true);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: OrdersService, useValue: { getOrder: api['getOrder'], receiveGoods: api['receiveGoods'], closeOrderShort: api['closeOrderShort'] } },
        { provide: InvoicesService, useValue: { listInvoices: api['listInvoices'] } },
        { provide: SuppliersService, useValue: { listSuppliers: api['listSuppliers'] } },
        { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId: 'tom', name: 'Tom' }) } },
        { provide: ConflictDialog, useValue: { show: vi.fn().mockResolvedValue(undefined) } },
        { provide: ConfirmDialog, useValue: { ask } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(OrderDetailPage);
    fixture.componentRef.setInput('id', 'o1');
    fixture.componentRef.setInput('order', loaded);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }
  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }

  it('shows the lines with ordered, received, invoiced and paid, the receipts and the links', async () => {
    const { el } = await setup(order({ status: 'PartiallyReceived', lines: [orderLine(1, 2, 2), orderLine(2, 1)], receipts: [receipt] }));
    const head = text(el.querySelector('header'));
    expect(head).toContain('PO-881');
    expect(head).toContain('Contoso Hardware');
    const cells = Array.from(el.querySelectorAll('[data-line="1"] td')).map((c) => text(c));
    expect(cells[0]).toContain('Item 1');
    expect(cells.slice(1)).toEqual(['2', '2', '2', '1']); // ordered, received, invoiced, paid
    expect(text(el.querySelector('[data-section="receipts"]'))).toContain('DN-5');
    expect(el.querySelector('a[href="/app/requests/r1"]')).not.toBeNull();
    expect(text(el.querySelector('[data-section="invoices"]'))).toContain('INV-77');
    expect(api['listInvoices']).toHaveBeenCalledTimes(1);
  });

  it('record delivery posts the entered quantities and shows the updated order', async () => {
    const { fixture, el } = await setup();
    api['getOrder'].mockResolvedValue(order({ status: 'PartiallyReceived', version: 2, lines: [orderLine(1, 2, 1), orderLine(2, 1)], receipts: [receipt] }));
    const inputs = Array.from(el.querySelectorAll<HTMLInputElement>('app-receipt-form input[type=number]'));
    inputs[0].value = '1';
    inputs[0].dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (el.querySelector('app-receipt-form button[type=submit]') as HTMLButtonElement).click();
    await settle(fixture);
    expect(api['receiveGoods']).toHaveBeenCalledWith(expect.objectContaining({ id: 'o1', 'If-Match': '"1"', body: { deliveryNote: null, lines: [{ lineNo: 1, quantity: 1 }] } }), expect.anything());
    expect(text(el.querySelector('[data-section="receipts"]'))).toContain('DN-5');
  });

  it('close_short_hidden_before_first_receipt', async () => {
    const before = await setup(order({ status: 'Confirmed' }));
    expect(text(before.el)).not.toContain('Close short');
    TestBed.resetTestingModule();
    const after = await setup(order({ status: 'PartiallyReceived', lines: [orderLine(1, 2, 1), orderLine(2, 1)], receipts: [receipt] }));
    expect(text(after.el)).toContain('Close short');
  });

  it('close short asks first, "no" sends nothing, "yes" closes the order', async () => {
    const { fixture, el } = await setup(order({ status: 'PartiallyReceived', lines: [orderLine(1, 2, 1), orderLine(2, 1)], receipts: [receipt] }));
    const button = () => el.querySelector('[data-action="close-short"] button') as HTMLButtonElement;
    api['getOrder'].mockResolvedValue(order({ status: 'ClosedShort', version: 3 }));
    ask.mockResolvedValueOnce(false);
    button().click();
    await settle(fixture);
    expect(api['closeOrderShort']).not.toHaveBeenCalled();
    button().click();
    await settle(fixture);
    expect(ask).toHaveBeenCalledWith(expect.objectContaining({ title: expect.stringContaining('PO-881') }));
    expect(api['closeOrderShort']).toHaveBeenCalledTimes(1);
    expect(text(el)).toContain('Closed short');
  });

  it('actions_hidden_without_orders_receive: no form, no close short', async () => {
    const { el } = await setup(order({ status: 'PartiallyReceived', lines: [orderLine(1, 2, 1), orderLine(2, 1)], receipts: [receipt] }), ['orders.read']);
    expect(el.querySelector('app-receipt-form')).toBeNull();
    expect(text(el)).not.toContain('Close short');
    expect(text(el)).not.toContain('Record delivery');
  });

  it('no form once everything is received, and no invoices section without invoices.read', async () => {
    const { el } = await setup(order({ status: 'Received', lines: [orderLine(1, 2, 2)], receipts: [receipt] }), ['orders.read', 'orders.receive']);
    expect(el.querySelector('app-receipt-form')).toBeNull();
    expect(el.querySelector('[data-section="invoices"]')).toBeNull();
    expect(api['listInvoices']).not.toHaveBeenCalled();
  });

  it('a supplier that is unavailable shows the attempts and the last error', async () => {
    const { el } = await setup(order({ status: 'PendingSupplier', supplierStatus: 'Supplier unavailable · retrying (attempt 4)', supplierAttempts: 3, supplierLastError: 'HTTP 503' }));
    expect(text(el)).toContain('retrying (attempt 4)');
    expect(text(el)).toContain('HTTP 503');
    expect(el.querySelector('app-receipt-form')).toBeNull(); // nothing can be received before the supplier confirms
  });
});
