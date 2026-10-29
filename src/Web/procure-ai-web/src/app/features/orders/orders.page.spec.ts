import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { SuppliersService } from '../../api/suppliers/services/suppliers.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { OrdersPage } from './orders.page';
import { order, orderLine, ordersPage } from './orders.testing';

describe('OrdersPage', () => {
  let listOrders: ReturnType<typeof vi.fn>;
  let listSuppliers: ReturnType<typeof vi.fn>;
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function setup(items = [order({ lines: [orderLine(1, 2, 2), orderLine(2, 1)], status: 'PartiallyReceived' })], permissions = ['orders.read', 'orders.receive']) {
    listOrders = vi.fn().mockResolvedValue(ordersPage(items));
    listSuppliers = vi.fn().mockResolvedValue({ items: [{ code: 'CH', name: 'Contoso Hardware', active: true, itemCount: 4 }], page: 1, pageSize: 100, total: 1 });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: OrdersService, useValue: { listOrders } },
        { provide: SuppliersService, useValue: { listSuppliers } },
        { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId: 'tom', name: 'Tom' }) } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(OrdersPage);
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('received_shows_x_of_y and the amount including VAT, with the supplier name from the cached supplier list', async () => {
    const { el } = await setup();
    const row = text(el.querySelector('tbody tr'));
    expect(row).toContain('PO-881');
    expect(row).toContain('Contoso Hardware');
    expect(row).toContain('R 86 940,00');
    expect(row).toContain('2 of 3');
    expect(listSuppliers).toHaveBeenCalledTimes(1);
    expect(el.querySelector('tbody tr a')?.getAttribute('href')).toBe('/app/orders/o1');
  });

  it('retrying_status_shows_attempts: the supplier line with its attempt, and the last error in an expander', async () => {
    const { el } = await setup([
      order({ status: 'PendingSupplier', supplierStatus: 'Supplier unavailable · retrying (attempt 3)', supplierAttempts: 2, supplierLastError: 'Supplier unavailable (HTTP 503) for placing the order.', lines: [orderLine(1, 6)] }),
    ]);
    const row = el.querySelector('tbody tr')!;
    expect(text(row)).toContain('Supplier unavailable · retrying (attempt 3)');
    expect(text(row)).toContain('0 of 6');
    expect(text(row.querySelector('details'))).toContain('2 failed attempts so far');
    expect(text(row.querySelector('details'))).toContain('Supplier unavailable (HTTP 503) for placing the order.');
    expect(row.querySelector('a.btn-secondary')).toBeNull(); // nothing to receive yet
  });

  it('offers Record delivery only with orders.receive and only where goods can be received', async () => {
    const confirmed = order({ id: 'o2', number: 'PO-882', status: 'Confirmed' });
    const done = order({ id: 'o3', number: 'PO-883', status: 'Received', lines: [orderLine(1, 2, 2)] });
    const withRight = await setup([confirmed, done]);
    const rows = Array.from(withRight.el.querySelectorAll('tbody tr'));
    expect(text(rows[0])).toContain('Record delivery');
    expect(text(rows[1])).not.toContain('Record delivery');
    TestBed.resetTestingModule();
    const without = await setup([confirmed], ['orders.read']);
    expect(text(without.el)).not.toContain('Record delivery');
  });

  it('shows the order status next to the number, apart from the supplier status', async () => {
    const { el } = await setup([order({ status: 'ClosedShort', supplierStatus: 'Confirmed, delivery 2026-11-03' })]);
    const cells = el.querySelectorAll('tbody tr td');
    expect(text(cells[0])).toContain('Closed short');
    expect(text(cells[3])).toContain('Confirmed, delivery 2026-11-03');
  });

  it('no button sits inside a link', async () => {
    const { el } = await setup();
    expect(el.querySelector('a button')).toBeNull();
  });

  it('filters by status and shows an empty state', async () => {
    const { fixture, el } = await setup();
    const select = el.querySelector('select') as HTMLSelectElement;
    listOrders.mockResolvedValue(ordersPage([]));
    select.value = 'Received';
    select.dispatchEvent(new Event('change'));
    for (let i = 0; i < 3; i++) {
      await fixture.whenStable();
      fixture.detectChanges();
    }
    expect(listOrders).toHaveBeenLastCalledWith({ page: 1, pageSize: 25, status: 'Received' });
    expect(text(el)).toContain('No orders match');
  });

  it('shows an error with Try again when loading fails', async () => {
    listOrders = vi.fn().mockRejectedValue(new Error('down'));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: OrdersService, useValue: { listOrders } },
        { provide: SuppliersService, useValue: { listSuppliers: vi.fn().mockResolvedValue({ items: [], page: 1, pageSize: 100, total: 0 }) } },
        { provide: AuthStore, useValue: { hasPermission: () => true, user: signal({ userId: 'tom', name: 'Tom' }) } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(OrdersPage);
    fixture.detectChanges();
    for (let i = 0; i < 4; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
    expect(text(fixture.nativeElement)).toContain('The orders could not be loaded.');
    expect(text(fixture.nativeElement)).toContain('Try again');
  });
});
