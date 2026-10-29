import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { InvoiceDraft, InvoiceFormStore } from './invoice-form.store';
import { mismatched } from './invoices.testing';
import { order, ordersPage } from '../orders/orders.testing';

describe('InvoiceFormStore', () => {
  let createInvoice: ReturnType<typeof vi.fn>;
  let listOrders: ReturnType<typeof vi.fn>;
  const draft = (o: Partial<InvoiceDraft> = {}): InvoiceDraft => ({
    orderId: 'o1', supplierInvoiceNumber: 'INV-1', invoiceDate: '2026-10-29', statedTotalInclVat: 57960,
    lines: [{ lineNo: 1, quantity: 2, unitPriceExclVat: 25200 }, { lineNo: 2, quantity: 1, unitPriceExclVat: 100 }], ...o,
  });

  beforeEach(() => {
    createInvoice = vi.fn().mockResolvedValue(mismatched());
    listOrders = vi.fn().mockResolvedValue(ordersPage([order()]));
    TestBed.configureTestingModule({
      providers: [InvoiceFormStore, { provide: InvoicesService, useValue: { createInvoice } }, { provide: OrdersService, useValue: { listOrders } }],
    });
  });
  const store = () => TestBed.inject(InvoiceFormStore);

  it('loads the orders the caller sees', async () => {
    await store().loadOrders();
    expect(listOrders).toHaveBeenCalledWith({ page: 1, pageSize: 100 });
    expect(store().orders()).toHaveLength(1);
  });

  it('leaves out cancelled, rejected and closed-short orders, and says when there are more orders than one page', async () => {
    listOrders.mockResolvedValue(ordersPage([order({ id: 'a', status: 'Confirmed' }), order({ id: 'b', status: 'Cancelled' }), order({ id: 'c', status: 'Rejected' }), order({ id: 'd', status: 'ClosedShort' }), order({ id: 'e', status: 'Received' })], 1, 250, 100));
    await store().loadOrders();
    expect(store().orders().map((o) => o.id)).toEqual(['a', 'e']);
    expect(store().moreOrders()).toBe(true);
  });

  it('a failure to load the orders sets an error', async () => {
    listOrders.mockRejectedValue(new Error('down'));
    await store().loadOrders();
    expect(store().error()).toBe('The orders could not be loaded.');
  });

  it('creates the invoice with a key, and a retry of the same data repeats it', async () => {
    createInvoice.mockRejectedValueOnce(new HttpErrorResponse({ status: 503 }));
    expect(await store().create(draft())).toBeNull();
    const created = await store().create(draft());
    expect(created?.id).toBe('i1');
    const [first, second] = createInvoice.mock.calls.map((c) => c[0]);
    expect(first['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(second['Idempotency-Key']).toBe(first['Idempotency-Key']);
    expect(second.body).toEqual({
      orderId: 'o1', supplierInvoiceNumber: 'INV-1', invoiceDate: '2026-10-29', statedTotalInclVat: 57960,
      lines: [{ lineNo: 1, quantity: 2, unitPriceExclVat: 25200 }, { lineNo: 2, quantity: 1, unitPriceExclVat: 100 }],
    });
    await store().create(draft({ supplierInvoiceNumber: 'INV-2' }));
    expect(createInvoice.mock.calls[2][0]['Idempotency-Key']).not.toBe(first['Idempotency-Key']);
  });

  it('a double click is one call', async () => {
    let finish!: (o: unknown) => void;
    createInvoice.mockReturnValueOnce(new Promise((r) => (finish = r)));
    const first = store().create(draft());
    const second = store().create(draft());
    expect(store().busy()).toBe(true);
    finish(mismatched());
    await Promise.all([first, second]);
    expect(createInvoice).toHaveBeenCalledTimes(1);
    expect(store().busy()).toBe(false);
  });

  it('dead_order_422_message_shown: the server message is the general error', async () => {
    createInvoice.mockRejectedValueOnce(new HttpErrorResponse({ status: 422, error: { title: 'Unprocessable', detail: 'PO-881 is cancelled, so it cannot be invoiced.' } }));
    expect(await store().create(draft())).toBeNull();
    expect(store().generalError()).toBe('PO-881 is cancelled, so it cannot be invoiced.');
    expect(store().busy()).toBe(false);
  });

  it('a 409 (same invoice number, other content) and a 404 show their messages', async () => {
    createInvoice.mockRejectedValueOnce(new HttpErrorResponse({ status: 409, error: { detail: 'Invoice INV-1 already exists with other content.' } }));
    await store().create(draft());
    expect(store().generalError()).toBe('Invoice INV-1 already exists with other content.');
    createInvoice.mockRejectedValueOnce(new HttpErrorResponse({ status: 404, error: { detail: 'Order o1 was not found.' } }));
    await store().create(draft());
    expect(store().generalError()).toBe('Order o1 was not found.');
  });

  it('400 field errors are keyed by field and by order line number; unplaced ones become the general message', async () => {
    createInvoice.mockRejectedValueOnce(
      new HttpErrorResponse({
        status: 400,
        error: { errors: { SupplierInvoiceNumber: ['Too long.'], 'Lines[1].Quantity': ['Must be at least 1.'], 'Lines[0].UnitPriceExclVat': ['Too high.'], StatedTotalInclVat: ['Not a total.'], Lines: ['Too many lines.'] } },
      }),
    );
    await store().create(draft());
    expect(store().fieldErrors()).toEqual({
      supplierInvoiceNumber: ['Too long.'],
      'line.2.quantity': ['Must be at least 1.'],
      'line.1.unitPrice': ['Too high.'],
      statedTotal: ['Not a total.'],
    });
    expect(store().generalError()).toBe('Too many lines.');
  });

  it('a generic failure gives a retry message, and clearErrors resets', async () => {
    createInvoice.mockRejectedValueOnce(new HttpErrorResponse({ status: 500 }));
    await store().create(draft());
    expect(store().generalError()).toBe('The invoice could not be saved. Please try again.');
    store().clearErrors();
    expect(store().generalError()).toBeNull();
  });
});
