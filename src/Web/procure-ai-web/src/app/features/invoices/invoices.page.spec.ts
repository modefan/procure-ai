import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { SuppliersService } from '../../api/suppliers/services/suppliers.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { invoiceDto } from '../requests/request-detail.testing';
import { InvoicesPage } from './invoices.page';
import { invoicesPage, mismatched } from './invoices.testing';

describe('InvoicesPage', () => {
  let listInvoices: ReturnType<typeof vi.fn>;
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function setup(items: InvoiceDto[] = [mismatched()], permissions = ['invoices.read', 'invoices.create']) {
    listInvoices = vi.fn().mockResolvedValue(invoicesPage(items));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: InvoicesService, useValue: { listInvoices } },
        { provide: SuppliersService, useValue: { listSuppliers: vi.fn().mockResolvedValue({ items: [{ code: 'CH', name: 'Contoso Hardware', active: true, itemCount: 4 }], page: 1, pageSize: 100, total: 1 }) } },
        { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId: 'thandi', name: 'Thandi' }) } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(InvoicesPage);
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('lists number, supplier name, order, amount, match badge and payment status', async () => {
    const { el } = await setup([mismatched(), invoiceDto({ id: 'i2', supplierInvoiceNumber: 'INV-5517', status: 'Paid', totalInclVatText: 'R 12 420,00' })]);
    const rows = Array.from(el.querySelectorAll('tbody tr'));
    expect(rows).toHaveLength(2);
    expect(text(rows[0])).toContain('INV-5521');
    expect(text(rows[0])).toContain('Contoso Hardware');
    expect(text(rows[0])).toContain('PO-881');
    expect(text(rows[0])).toContain('R 86 940,00');
    expect(text(rows[0])).toContain('Mismatched');
    expect(text(rows[0])).toContain('Unpaid');
    expect(text(rows[1])).toContain('Matched');
    expect(text(rows[1])).toContain('Paid');
    expect(rows[0].querySelector('a')?.getAttribute('href')).toBe('/app/invoices/i1');
  });

  it('shows held invoices as held and partly paid ones as partially paid', async () => {
    const { el } = await setup([mismatched({ onHold: true }), mismatched({ id: 'i3', status: 'PartiallyPaid' })]);
    const rows = Array.from(el.querySelectorAll('tbody tr'));
    expect(text(rows[0])).toContain('Held');
    expect(text(rows[1])).toContain('Partially paid');
  });

  it('filters by status and offers manual entry only with invoices.create', async () => {
    const withRight = await setup();
    const select = withRight.el.querySelector('#invoice-status') as HTMLSelectElement;
    select.value = 'Mismatched';
    select.dispatchEvent(new Event('change', { bubbles: true }));
    await vi.waitFor(() => expect(listInvoices).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, status: 'Mismatched' }));
    expect(withRight.el.querySelector('a[href="/app/invoices/new"]')).not.toBeNull();
    TestBed.resetTestingModule();
    const without = await setup([mismatched()], ['invoices.read']);
    expect(without.el.querySelector('a[href="/app/invoices/new"]')).toBeNull();
  });

  it('says so when there are no invoices, and when the filter matches none', async () => {
    const { el } = await setup([]);
    expect(text(el)).toContain('No invoices yet');
  });

  it('shows an error with Try again when the list cannot be loaded', async () => {
    listInvoices = vi.fn().mockRejectedValue(new Error('down'));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: InvoicesService, useValue: { listInvoices } },
        { provide: SuppliersService, useValue: { listSuppliers: vi.fn().mockResolvedValue({ items: [], page: 1, pageSize: 100, total: 0 }) } },
        { provide: AuthStore, useValue: { hasPermission: () => true, user: signal({ userId: 'thandi', name: 'Thandi' }) } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(InvoicesPage);
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
    expect(text(fixture.nativeElement.querySelector('[role="alert"]'))).toContain('The invoices could not be loaded.');
  });
});
