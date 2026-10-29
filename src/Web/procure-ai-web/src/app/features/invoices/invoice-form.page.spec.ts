import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { SuppliersService } from '../../api/suppliers/services/suppliers.service';
import { order, orderLine, ordersPage } from '../orders/orders.testing';
import { InvoiceFormPage } from './invoice-form.page';
import { mismatched } from './invoices.testing';

describe('InvoiceFormPage', () => {
  let createInvoice: ReturnType<typeof vi.fn>;
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const set = (input: HTMLInputElement | HTMLSelectElement, value: string) => {
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input', { bubbles: true }));
    input.dispatchEvent(new Event('input', { bubbles: true }));
  };
  const field = (el: HTMLElement, id: string) => el.querySelector(`#${id}`) as HTMLInputElement & HTMLSelectElement;
  const qty = (el: HTMLElement, lineNo: number) => el.querySelector(`[data-line="${lineNo}"] input[data-field="quantity"]`) as HTMLInputElement;
  const price = (el: HTMLElement, lineNo: number) => el.querySelector(`[data-line="${lineNo}"] input[data-field="price"]`) as HTMLInputElement;
  const click = (el: Element | null) => (el as HTMLElement).click();

  // Order: 3 laptops at R 25 200,00 (2 received) and 1 mouse at R 100,00.
  const laptops = order({
    id: 'o1', number: 'PO-881', status: 'PartiallyReceived',
    lines: [orderLine(1, 3, 2, { description: 'Laptop 14"', unitPriceExclVat: 25200 }), orderLine(2, 1, 0, { description: 'Mouse', unitPriceExclVat: 100 })],
  });
  const dead = order({ id: 'o9', number: 'PO-900', status: 'Cancelled' });

  async function setup() {
    createInvoice = vi.fn().mockResolvedValue(mismatched({ id: 'i77' }));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', children: [] }]),
        { provide: InvoicesService, useValue: { createInvoice } },
        { provide: OrdersService, useValue: { listOrders: vi.fn().mockResolvedValue(ordersPage([laptops, dead])) } },
        { provide: SuppliersService, useValue: { listSuppliers: vi.fn().mockResolvedValue({ items: [{ code: 'CH', name: 'Contoso Hardware', active: true, itemCount: 4 }], page: 1, pageSize: 100, total: 1 }) } },
      ],
    });
    const fixture = TestBed.createComponent(InvoiceFormPage);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance };
  }
  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 4; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }
  async function choose(ctx: Awaited<ReturnType<typeof setup>>, orderId = 'o1') {
    set(field(ctx.el, 'invoice-order'), orderId);
    await settle(ctx.fixture);
  }

  it('lists the orders with number, supplier and status, and prefills one row per order line with the order price', async () => {
    const ctx = await setup();
    const options = Array.from(ctx.el.querySelectorAll('#invoice-order option')).map((o) => text(o));
    expect(options.some((o) => o.includes('PO-881') && o.includes('Contoso Hardware') && o.includes('Partially received'))).toBe(true);
    await choose(ctx);
    const rows = ctx.el.querySelectorAll('[data-line]');
    expect(rows).toHaveLength(2);
    expect(qty(ctx.el, 1).value).toBe('2'); // what was received
    expect((qty(ctx.el, 2)).value).toBe('1'); // nothing received yet: what was ordered
    expect(price(ctx.el, 2).value).toBe('100');
  });

  it('manual_entry_vat_on_total: VAT is shown once, on the total, with excl. and incl. totals', async () => {
    const ctx = await setup();
    await choose(ctx);
    set(qty(ctx.el, 1), '2');
    set(qty(ctx.el, 2), '0');
    await settle(ctx.fixture);
    const totals = text(ctx.el.querySelector('[data-testid="totals"]'));
    expect(totals).toContain('R 50 400,00');
    expect(totals).toContain('VAT 15 %');
    expect(totals).toContain('R 7 560,00');
    expect(totals).toContain('R 57 960,00');
  });

  it('sends only lines with a quantity, the typed number and date, and the total shown; then opens the invoice', async () => {
    const ctx = await setup();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await choose(ctx);
    set(field(ctx.el, 'invoice-number'), '  INV-5521 ');
    set(field(ctx.el, 'invoice-date'), '2026-10-28');
    set(qty(ctx.el, 2), '0');
    await settle(ctx.fixture);
    click(ctx.el.querySelector('[data-testid="save-invoice"] button'));
    await settle(ctx.fixture);
    expect(createInvoice).toHaveBeenCalledTimes(1);
    const params = createInvoice.mock.calls[0][0];
    expect(params['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(params.body).toEqual({
      orderId: 'o1', supplierInvoiceNumber: 'INV-5521', invoiceDate: '2026-10-28', statedTotalInclVat: 57960,
      lines: [{ lineNo: 1, quantity: 2, unitPriceExclVat: 25200 }],
    });
    expect(navigate).toHaveBeenCalledWith(['/app/invoices', 'i77']);
  });

  it('a total typed from the invoice is sent instead of the computed one', async () => {
    const ctx = await setup();
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await choose(ctx);
    set(field(ctx.el, 'invoice-number'), 'INV-9');
    set(field(ctx.el, 'invoice-stated-total'), '58000,50');
    await settle(ctx.fixture);
    click(ctx.el.querySelector('[data-testid="save-invoice"] button'));
    await settle(ctx.fixture);
    expect(createInvoice.mock.calls[0][0].body.statedTotalInclVat).toBe(58000.5);
  });

  it('refuses an empty form: asks for an order and a number, sends nothing', async () => {
    const ctx = await setup();
    click(ctx.el.querySelector('[data-testid="save-invoice"] button'));
    await settle(ctx.fixture);
    expect(createInvoice).not.toHaveBeenCalled();
    const page = text(ctx.el);
    expect(page).toContain('Choose an order');
    expect(page).toContain('Enter the supplier invoice number');
  });

  it('needs at least one quantity above zero', async () => {
    const ctx = await setup();
    await choose(ctx);
    set(field(ctx.el, 'invoice-number'), 'INV-1');
    set(qty(ctx.el, 1), '0');
    set(qty(ctx.el, 2), '0');
    await settle(ctx.fixture);
    click(ctx.el.querySelector('[data-testid="save-invoice"] button'));
    await settle(ctx.fixture);
    expect(createInvoice).not.toHaveBeenCalled();
    expect(text(ctx.el)).toContain('Enter a quantity for at least one item.');
  });

  it('dead_order_422_message_shown: an order that cannot be invoiced shows the server message above the form', async () => {
    const ctx = await setup();
    createInvoice.mockRejectedValueOnce(new HttpErrorResponse({ status: 422, error: { detail: 'PO-900 is cancelled, so it cannot be invoiced.' } }));
    await choose(ctx, 'o9');
    set(field(ctx.el, 'invoice-number'), 'INV-1');
    set(qty(ctx.el, 1), '1');
    await settle(ctx.fixture);
    click(ctx.el.querySelector('[data-testid="save-invoice"] button'));
    await settle(ctx.fixture);
    expect(text(ctx.el.querySelector('[role="alert"]'))).toBe('PO-900 is cancelled, so it cannot be invoiced.');
  });

  it('shows a server field error under its field', async () => {
    const ctx = await setup();
    createInvoice.mockRejectedValueOnce(new HttpErrorResponse({ status: 400, error: { errors: { SupplierInvoiceNumber: ['The number is too long.'] } } }));
    await choose(ctx);
    set(field(ctx.el, 'invoice-number'), 'INV-1');
    await settle(ctx.fixture);
    click(ctx.el.querySelector('[data-testid="save-invoice"] button'));
    await settle(ctx.fixture);
    expect(text(ctx.el.querySelector('#invoice-number-error'))).toBe('The number is too long.');
  });

  it('has unsaved changes once something was typed, and none after a fresh start', async () => {
    const ctx = await setup();
    expect(ctx.page.hasUnsavedChanges()).toBe(false);
    set(field(ctx.el, 'invoice-number'), 'INV-1');
    await settle(ctx.fixture);
    expect(ctx.page.hasUnsavedChanges()).toBe(true);
  });
});
