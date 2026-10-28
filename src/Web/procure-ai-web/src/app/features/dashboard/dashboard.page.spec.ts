import { TestBed } from '@angular/core/testing';
import { DashboardPage } from './dashboard.page';
import { DashboardHarness, setupDashboard } from './dashboard.testing';

describe('DashboardPage', () => {
  let h: DashboardHarness;
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function render(permissions: string[], costCenters?: string[]) {
    h = await setupDashboard(permissions, costCenters);
    const fixture = TestBed.createComponent(DashboardPage);
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  afterEach(() => void h?.live.stop());

  it('tiles_follow_permissions: a requester sees no approval, budget or invoice tile', async () => {
    const { el } = await render(['requests.read', 'requests.create']);
    expect(text(el)).toContain('My open requests');
    expect(text(el)).not.toContain('Waiting for my approval');
    expect(text(el)).not.toContain('budget');
    expect(text(el)).not.toContain('Invoices with a mismatch');
    expect(text(el)).toContain('Recent activity');
  });

  it('a manager sees every tile with its numbers', async () => {
    const { el } = await render(['requests.read', 'requests.approve', 'budgets.read', 'invoices.read']);
    expect(text(el.querySelector('[data-tile="approvals"]'))).toContain('3');
    expect(text(el.querySelector('[data-tile="approvals"]'))).toContain('Escalates');
    expect(text(el.querySelector('[data-tile="my-open"]'))).toContain('5');
    const budget = text(el.querySelector('[data-tile="budget-IT-200"]'));
    expect(budget).toContain('IT-200');
    expect(budget).toContain('62%');
    expect(budget).toContain('R 558 000,00');
    expect(budget).toContain('R 75 600,00');
    expect(budget).toContain('R 900 000,00');
    expect(text(el.querySelector('[data-tile="mismatched"]'))).toContain('1');
    expect(el.querySelectorAll('[aria-label="Recent activity"] li')).toHaveLength(2);
    expect(el.querySelector('[data-tile="approvals"] a')?.getAttribute('href')).toBe('/app/approvals');
  });

  it('online_shows_three_names_and_more', async () => {
    const { fixture, el } = await render(['requests.read']);
    expect(text(el.querySelector('[aria-label="Online now"]'))).toContain('Nobody else is online');

    h.online.set([{ userId: 'a', name: 'Ana' }, { userId: 'b', name: 'Ben' }, { userId: 'c', name: 'Cara' }, { userId: 'd', name: 'Dan' }, { userId: 'e', name: 'Eve' }]);
    fixture.detectChanges();
    expect(text(el.querySelector('[aria-label="Online now"]'))).toBe('Online now: Ana, Ben, Cara +2');

    h.online.set([{ userId: 'a', name: 'Ana' }, { userId: 'b', name: 'Ben' }]);
    fixture.detectChanges();
    expect(text(el.querySelector('[aria-label="Online now"]'))).toBe('Online now: Ana, Ben');
  });

  it('the approvals tile shows an error with a retry, not 0, when the inbox could not be loaded', async () => {
    h = await setupDashboard(['requests.read', 'requests.approve']);
    h.api.getApprovalInbox.mockRejectedValue(new Error('down'));
    const fixture = TestBed.createComponent(DashboardPage);
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
    const el = fixture.nativeElement as HTMLElement;
    const tile = text(el.querySelector('[data-tile="approvals"]'));
    expect(tile).toContain('Could not be loaded');
    expect(tile).not.toMatch(/0/);

    h.api.getApprovalInbox.mockResolvedValue({ items: [], page: 1, pageSize: 25, total: 4 });
    (el.querySelector('[data-tile="approvals"] button') as HTMLButtonElement).click();
    for (let i = 0; i < 4; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
    expect(text(el.querySelector('[data-tile="approvals"]'))).toContain('4');
  });

  it('no tile has a button inside a link', async () => {
    const { el } = await render(['requests.read', 'requests.approve', 'budgets.read', 'invoices.read']);
    expect(el.querySelectorAll('a button')).toHaveLength(0);
  });

  it('a tile that failed to load shows a retry instead of a number', async () => {
    h = await setupDashboard(['requests.read', 'invoices.read']);
    h.api.listInvoices.mockRejectedValue(new Error('down'));
    const fixture = TestBed.createComponent(DashboardPage);
    fixture.detectChanges();
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el.querySelector('[data-tile="mismatched"]'))).toContain('Could not be loaded');
    expect(text(el.querySelector('[data-tile="my-open"]'))).toContain('5');
  });
});
