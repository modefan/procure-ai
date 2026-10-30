import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { SuppliersService } from '../../api/suppliers/services/suppliers.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { formatRand } from '../../core/ui/money.pipe';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { invoiceDto } from '../requests/request-detail.testing';
import { InvoiceDetailPage } from './invoice-detail.page';
import { invoiceLine, mismatched, payment } from './invoices.testing';

const FINANCE = ['invoices.read', 'invoices.release-payment', 'orders.read', 'requests.read'];

describe('InvoiceDetailPage', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  /** `text()` turns no-break spaces into plain ones; so must the expected amount. */
  const spaced = (value: string) => value.replace(/\s+/g, ' ');
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  /** Clicks the button an action marker points at (the marker may be the button or hold it). */
  const press = (el: Element, action: string) => {
    const marker = el.querySelector(`[data-action="${action}"]`) as HTMLElement;
    (marker.matches('button') ? marker : (marker.querySelector('button') as HTMLElement)).click();
  };

  async function setup(loaded: InvoiceDto = mismatched(), permissions = FINANCE, failing = false) {
    api = {
      getInvoice: failing ? vi.fn().mockRejectedValue(new Error('down')) : vi.fn().mockResolvedValue(loaded),
      releasePayment: vi.fn().mockResolvedValue(mismatched({ status: 'PartiallyPaid', version: 2, payments: [payment()], payableInclVat: 0, payableInclVatText: 'R 0,00' })),
      holdInvoice: vi.fn().mockResolvedValue(mismatched({ onHold: true, holdReason: 'Bank details changed', version: 2, payableInclVat: 0, payableInclVatText: 'R 0,00' })),
      releaseInvoiceHold: vi.fn().mockResolvedValue(mismatched({ version: 3 })),
      listSuppliers: vi.fn().mockResolvedValue({ items: [{ code: 'CH', name: 'Contoso Hardware', active: true, itemCount: 4 }], page: 1, pageSize: 100, total: 1 }),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: InvoicesService, useValue: { getInvoice: api['getInvoice'], releasePayment: api['releasePayment'], holdInvoice: api['holdInvoice'], releaseInvoiceHold: api['releaseInvoiceHold'] } },
        { provide: SuppliersService, useValue: { listSuppliers: api['listSuppliers'] } },
        { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId: 'thandi', name: 'Thandi' }) } },
        { provide: ConflictDialog, useValue: { show: vi.fn().mockResolvedValue(undefined) } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(InvoiceDetailPage);
    fixture.componentRef.setInput('id', 'i1');
    fixture.componentRef.setInput('invoice', failing ? null : loaded);
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

  it('shows ordered, received and invoiced per line with the result text, the VAT and the totals', async () => {
    const { el } = await setup();
    const head = text(el.querySelector('header'));
    expect(head).toContain('INV-5521');
    expect(head).toContain('Contoso Hardware');
    expect(head).toContain('Mismatched');
    const row = text(el.querySelector('[data-line="1"]'));
    expect(row).toContain('Laptop 14"');
    expect(row).toContain('3 × R 25 200,00'); // ordered
    expect(row).toContain('Invoiced 1 more than received');
    const totals = text(el.querySelector('[data-section="totals"]'));
    expect(totals).toContain('R 75 600,00');
    expect(totals).toContain('VAT');
    expect(totals).toContain('R 11 340,00');
    expect(totals).toContain('R 86 940,00');
    expect(text(el.querySelector('[data-section="reasons"]'))).toContain('only 2 received');
  });

  it('pay_button_shows_payable_amount: the wireframe\'s "Approve partial payment (R 57 960,00)"', async () => {
    const { el } = await setup();
    const pay = el.querySelector('[data-action="pay-all"]');
    expect(text(pay)).toBe(`Approve partial payment (${mismatched().payableInclVatText})`);
    expect(text(pay)).toContain('R 57 960,00');
    expect(el.querySelector('[data-action="hold"]')).not.toBeNull();
  });

  it('a fully payable invoice says "Pay invoice"', async () => {
    const { el } = await setup(invoiceDto({ status: 'Matched', lines: [invoiceLine(1)], payableInclVat: 86940, payableInclVatText: 'R 86 940,00' }));
    expect(text(el.querySelector('[data-action="pay-all"]'))).toBe('Pay invoice (R 86 940,00)');
  });

  it('pay_hidden_without_release_payment: no pay, hold or release buttons, but the match stays visible', async () => {
    const { el } = await setup(mismatched(), ['invoices.read']);
    expect(el.querySelector('[data-action="pay-all"]')).toBeNull();
    expect(el.querySelector('[data-action="hold"]')).toBeNull();
    expect(el.querySelector('[data-action="release-hold"]')).toBeNull();
    expect(el.querySelector('[data-action="pay-chosen"]')).toBeNull();
    expect(text(el.querySelector('[data-line="1"]'))).toContain('Invoiced 1 more than received');
  });

  it('pays all payable with If-Match, shows the busy state, and then the new payment with its ERP status', async () => {
    const { fixture, el } = await setup(mismatched({ version: 6 }));
    press(el, 'pay-all');
    fixture.detectChanges();
    await settle(fixture);
    expect(api['releasePayment']).toHaveBeenCalledTimes(1);
    expect(api['releasePayment'].mock.calls[0][0]).toMatchObject({ id: 'i1', 'If-Match': '"6"', body: { lines: [{ lineNo: 1, quantity: 2 }] } });
    const payments = text(el.querySelector('[data-section="payments"]'));
    expect(payments).toContain('PAY-0001');
    expect(payments).toContain('R 57 960,00');
    expect(payments).toContain('Exported');
    expect(payments).toContain('ERP-5521');
    expect(el.querySelector('[data-action="pay-all"]')).toBeNull(); // nothing payable any more
  });

  it('a payment not yet exported says so, with the attempts and the last error', async () => {
    const pending = payment({ exportStatus: 'Pending', erpReference: null, exportAttempts: 2, lastExportError: 'ERP unavailable (HTTP 503)' });
    const { el } = await setup(mismatched({ status: 'PartiallyPaid', payments: [pending], payableInclVat: 0, payableInclVatText: 'R 0,00' }));
    const payments = text(el.querySelector('[data-section="payments"]'));
    expect(payments).toContain('Export pending');
    expect(payments).toContain('ERP unavailable (HTTP 503)');
  });

  it('chosen quantities: each line up to what is payable, sent as lines', async () => {
    const { fixture, el } = await setup();
    press(el, 'choose-quantities');
    fixture.detectChanges();
    const input = el.querySelector('input[aria-label^="Quantity to pay"]') as HTMLInputElement;
    expect(input).not.toBeNull();
    input.value = '1';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    press(el, 'pay-chosen');
    await settle(fixture);
    expect(api['releasePayment'].mock.calls[0][0].body).toEqual({ lines: [{ lineNo: 1, quantity: 1 }] });
  });

  it('refuses more than payable on the chosen quantities, sending nothing', async () => {
    const { fixture, el } = await setup();
    press(el, 'choose-quantities');
    fixture.detectChanges();
    const input = el.querySelector('input[aria-label^="Quantity to pay"]') as HTMLInputElement;
    input.value = '3';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    press(el, 'pay-chosen');
    await settle(fixture);
    expect(api['releasePayment']).not.toHaveBeenCalled();
    expect(text(el)).toContain('Only 2 can be paid');
  });

  it('the chosen-quantities button shows what the quantities cost, never more than can be paid', async () => {
    const { fixture, el } = await setup();
    press(el, 'choose-quantities');
    fixture.detectChanges();
    const input = el.querySelector('input[aria-label^="Quantity to pay"]') as HTMLInputElement;
    const label = () => text(el.querySelector('[data-action="pay-chosen"]'));
    input.value = '1';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    expect(label()).toBe(`Pay chosen quantities (${spaced(formatRand(28980))})`); // 1 x R 25 200,00 + 15 % VAT
    input.value = '9';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    expect(label()).toBe(`Pay chosen quantities (${spaced(formatRand(57960))})`); // clamped to the 2 that can be paid
  });

  it('hold asks for a reason first (at least 3 characters), then holds with If-Match', async () => {
    const { fixture, el } = await setup(mismatched({ version: 4 }));
    press(el, 'hold');
    fixture.detectChanges();
    press(el, 'confirm-hold');
    await settle(fixture);
    expect(api['holdInvoice']).not.toHaveBeenCalled();
    expect(text(el)).toContain('Enter a reason');
    const reason = el.querySelector('#hold-reason') as HTMLTextAreaElement;
    reason.value = 'Bank details changed';
    reason.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    press(el, 'confirm-hold');
    await settle(fixture);
    expect(api['holdInvoice'].mock.calls[0][0]).toMatchObject({ id: 'i1', 'If-Match': '"4"', body: { reason: 'Bank details changed' } });
    expect(text(el.querySelector('header'))).toContain('Held');
    expect(text(el.querySelector('[data-section="hold"]'))).toContain('Bank details changed');
    expect(el.querySelector('[data-action="pay-all"]')).toBeNull();
    expect(el.querySelector('[data-action="release-hold"]')).not.toBeNull();
  });

  it('release hold sends the request and shows the invoice again', async () => {
    const { fixture, el } = await setup(mismatched({ onHold: true, holdReason: 'Bank details changed', payableInclVat: 0, payableInclVatText: 'R 0,00' }));
    press(el, 'release-hold');
    await settle(fixture);
    expect(api['releaseInvoiceHold']).toHaveBeenCalledTimes(1);
    expect(el.querySelector('[data-section="hold"]')).toBeNull();
  });

  it('a refused payment shows the server message on the page', async () => {
    const { fixture, el } = await setup();
    api['releasePayment'].mockRejectedValueOnce(
      new HttpErrorResponse({ status: 403, error: { detail: 'You raised this request, so you cannot release its payment.' } }),
    );
    press(el, 'pay-all');
    await settle(fixture);
    expect(text(el.querySelector('[role="alert"]'))).toBe('You raised this request, so you cannot release its payment.');
  });

  it('nothing payable yet: says why instead of offering a payment', async () => {
    const { el } = await setup(mismatched({ payableInclVat: 0, payableInclVatText: 'R 0,00', lines: [invoiceLine(1, { received: 0, quantityOk: false, payable: 0 })] }));
    expect(el.querySelector('[data-action="pay-all"]')).toBeNull();
    expect(text(el.querySelector('[data-section="actions"]'))).toContain('Nothing can be paid yet');
    expect(el.querySelector('[data-action="hold"]')).not.toBeNull();
  });

  it('a paid invoice offers no hold, and says it is paid in full', async () => {
    const { el } = await setup(invoiceDto({ status: 'Paid', lines: [invoiceLine(1, { paid: 3, payable: 0 })], payments: [payment()] }));
    expect(el.querySelector('[data-action="hold"]')).toBeNull();
    expect(text(el.querySelector('[data-section="actions"]'))).toContain('paid in full');
  });

  it('says "released by you" for the caller, names a readable releaser and never shows a user ID', async () => {
    const mine = await setup(mismatched({ payments: [payment({ releasedBy: 'thandi' })] }));
    expect(text(mine.el.querySelector('[data-section="payments"]'))).toContain('released by you');
    TestBed.resetTestingModule();
    const named = await setup(mismatched({ payments: [payment({ releasedBy: 'Ben Finance' })] }));
    expect(text(named.el.querySelector('[data-section="payments"]'))).toContain('released by Ben Finance');
    TestBed.resetTestingModule();
    const id = await setup(mismatched({ payments: [payment({ releasedBy: '00000000-0000-4000-8000-000000000009' })] }));
    expect(text(id.el.querySelector('[data-section="payments"]'))).not.toContain('00000000');
    expect(text(id.el.querySelector('[data-section="payments"]'))).not.toContain('released by');
  });

  it('payment_shows_released_by_name', async () => {
    const uuid = '00000000-0000-4000-8000-000000000009';
    const named = await setup(mismatched({ payments: [payment({ releasedBy: uuid, releasedByName: 'Ben Finance' })] }));
    expect(text(named.el.querySelector('[data-section="payments"]'))).toContain('released by Ben Finance');
    expect(text(named.el.querySelector('[data-section="payments"]'))).not.toContain('00000000');
    TestBed.resetTestingModule();
    const mine = await setup(mismatched({ payments: [payment({ releasedBy: 'thandi', releasedByName: 'Thandi Finance' })] }));
    expect(text(mine.el.querySelector('[data-section="payments"]'))).toContain('released by you');
    TestBed.resetTestingModule();
    const unknown = await setup(mismatched({ payments: [payment({ releasedBy: uuid, releasedByName: null })] }));
    expect(text(unknown.el.querySelector('[data-section="payments"]'))).not.toContain('released by');
  });

  it('links to the order only for callers who may read orders', async () => {
    const withOrders = await setup();
    expect(withOrders.el.querySelector('a[href="/app/orders/o1"]')).not.toBeNull();
    TestBed.resetTestingModule();
    const without = await setup(mismatched(), ['invoices.read']);
    expect(without.el.querySelector('a[href="/app/orders/o1"]')).toBeNull();
    expect(text(without.el)).toContain('PO-881');
  });

  it('shows an error with a retry when the invoice cannot be loaded', async () => {
    const { el } = await setup(null as unknown as InvoiceDto, FINANCE, true);
    expect(text(el.querySelector('[role="alert"]'))).toContain('could not be loaded');
    expect(el.querySelector('[role="alert"] button')).not.toBeNull();
  });
});
