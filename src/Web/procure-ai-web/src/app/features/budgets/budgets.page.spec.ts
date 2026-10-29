import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { BudgetDto } from '../../api/budgets/models/budget-dto';
import { BudgetsService } from '../../api/budgets/services/budgets.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { BudgetsPage } from './budgets.page';
import { budgetDto } from './budgets.testing';

describe('BudgetsPage', () => {
  let api: { listBudgets: ReturnType<typeof vi.fn>; setBudget: ReturnType<typeof vi.fn> };
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  beforeEach(() => {
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    };
  });

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }

  async function setup(items: BudgetDto[] = [budgetDto(), budgetDto('MK-110', { amount: 360000, spent: 160200, reserved: 0, available: 199800, ownerName: 'Ben Marketing' })], permissions = ['budgets.read', 'budgets.manage']) {
    api = { listBudgets: vi.fn().mockResolvedValue(items), setBudget: vi.fn().mockResolvedValue(budgetDto('IT-200', { amount: 950000, available: 316400, version: 4 })) };
    TestBed.configureTestingModule({
      providers: [
        { provide: BudgetsService, useValue: api },
        { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId: 'thandi', name: 'Thandi' }) } },
        { provide: ConflictDialog, useValue: { show: vi.fn().mockResolvedValue(undefined) } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(BudgetsPage);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  const row = (el: HTMLElement, code: string) => el.querySelector(`tbody tr[data-budget="${code}"]`);

  it('available_shown_from_api: cost center, owner, budget, spent, reserved and the available amount the server sent', async () => {
    // The server's available is shown as sent, not worked out here (it need not equal budget - spent - reserved).
    const { el } = await setup([budgetDto('IT-200', { available: 123456 }), budgetDto('MK-110', { amount: 360000, spent: 160200, reserved: 0, available: 199800, ownerName: 'Ben Marketing' })]);
    const it = text(row(el, 'IT-200'));
    expect(it).toContain('IT-200');
    expect(it).toContain('Maria Manager');
    expect(it).toContain('R 900 000,00');
    expect(it).toContain('R 558 000,00');
    expect(it).toContain('R 75 600,00');
    expect(it).toContain('R 123 456,00');
    expect(it).not.toContain('R 266 400,00');
    expect(text(row(el, 'MK-110'))).toContain('R 199 800,00');
    expect(text(row(el, 'MK-110'))).toContain('Ben Marketing');
  });

  it('shows the fiscal year as FY 2026/27 and asks for the current one', async () => {
    const { el } = await setup();
    expect(text(el)).toContain('FY 2026/27');
    expect(api.listBudgets).toHaveBeenCalledWith({ fiscalYear: expect.stringMatching(/^FY\d{4}$/) });
  });

  it('shows a dash when a cost center has no owner', async () => {
    const { el } = await setup([budgetDto('IT-200', { ownerName: null })]);
    expect(text(row(el, 'IT-200')?.querySelector('[data-cell="owner"]') ?? null)).toBe('Not set');
  });

  it('edit_hidden_without_manage: no Edit button for a reader', async () => {
    const withRight = await setup();
    expect(withRight.el.querySelector('[data-action="edit"]')).not.toBeNull();
    TestBed.resetTestingModule();
    const without = await setup(undefined, ['budgets.read']);
    expect(without.el.querySelectorAll('tbody tr')).toHaveLength(2);
    expect(without.el.querySelector('[data-action="edit"]')).toBeNull();
  });

  it('Edit opens a dialog with the current amount; Save sends it with If-Match and closes', async () => {
    const { fixture, el } = await setup();
    (row(el, 'IT-200')!.querySelector('[data-action="edit"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await settle(fixture);
    const dialog = el.querySelector('dialog') as HTMLDialogElement;
    expect(dialog.hasAttribute('open')).toBe(true);
    const input = dialog.querySelector('#budget-amount') as HTMLInputElement;
    expect(input.value).toBe('900000');
    expect(text(dialog)).toContain('IT-200');
    expect(text(dialog)).toContain('R 633 600,00');
    input.value = '950000';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    (dialog.querySelector('[data-action="save-budget"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(api.setBudget).toHaveBeenCalledWith({ costCenter: 'IT-200', fiscalYear: 'FY2027', 'If-Match': '"3"', body: { amount: 950000 } });
    expect(el.querySelector('dialog[open]')).toBeNull();
  });

  it('edit_422_message_shown: the refusal stays in the open dialog', async () => {
    const { fixture, el } = await setup();
    api.setBudget.mockRejectedValue(new HttpErrorResponse({ status: 422, error: { detail: 'The budget cannot go below R 633 600,00 (spent plus reserved).' } }));
    (row(el, 'IT-200')!.querySelector('[data-action="edit"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await settle(fixture);
    const dialog = el.querySelector('dialog') as HTMLDialogElement;
    const input = dialog.querySelector('#budget-amount') as HTMLInputElement;
    input.value = '100000';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    (dialog.querySelector('[data-action="save-budget"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(dialog.querySelector('[role="alert"]'))).toContain('The budget cannot go below R 633 600,00');
    expect(dialog.hasAttribute('open')).toBe(true);
    // The R stays with its number, so a narrow screen never breaks the line between them.
    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain('R 633');
  });

  it('after a 412 the open dialog shows the latest amount and says the figures changed', async () => {
    const { fixture, el } = await setup();
    (row(el, 'IT-200')!.querySelector('[data-action="edit"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await settle(fixture);
    const dialog = el.querySelector('dialog') as HTMLDialogElement;
    const input = dialog.querySelector('#budget-amount') as HTMLInputElement;
    input.value = '940000';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    api.setBudget.mockRejectedValue(new HttpErrorResponse({ status: 412 }));
    api.listBudgets.mockResolvedValue([budgetDto('IT-200', { amount: 920000, version: 5 })]);
    (dialog.querySelector('[data-action="save-budget"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(dialog.hasAttribute('open')).toBe(true);
    expect((dialog.querySelector('#budget-amount') as HTMLInputElement).value).toBe('920000');
    expect(text(dialog)).toContain('The figures were just updated');
  });

  it('an empty amount is refused before anything is sent', async () => {
    const { fixture, el } = await setup();
    (row(el, 'IT-200')!.querySelector('[data-action="edit"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await settle(fixture);
    const dialog = el.querySelector('dialog') as HTMLDialogElement;
    const input = dialog.querySelector('#budget-amount') as HTMLInputElement;
    input.value = '';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    (dialog.querySelector('[data-action="save-budget"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(api.setBudget).not.toHaveBeenCalled();
    expect(text(dialog)).toContain('Enter an amount');
  });

  it('shows an empty state, a loading skeleton and a load error with Try again', async () => {
    const empty = await setup([]);
    expect(text(empty.el)).toContain('No budgets');
    TestBed.resetTestingModule();
    const failed = await setup();
    api.listBudgets.mockRejectedValue(new Error('down'));
    TestBed.resetTestingModule();
    api = { listBudgets: vi.fn().mockRejectedValue(new Error('down')), setBudget: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: BudgetsService, useValue: api },
        { provide: AuthStore, useValue: { hasPermission: () => true, user: signal({ userId: 'thandi', name: 'Thandi' }) } },
        { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
      ],
    });
    const fixture = TestBed.createComponent(BudgetsPage);
    fixture.detectChanges();
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el.querySelector('[role="alert"]'))).toContain('The budgets could not be loaded.');
    expect(el.textContent).toContain('Try again');
    void failed;
  });
});
