import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { BudgetsService } from '../../api/budgets/services/budgets.service';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { currentFiscalYear } from '../dashboard/dashboard.store';
import { BudgetsStore } from './budgets.store';
import { budgetDto } from './budgets.testing';

describe('BudgetsStore', () => {
  let api: { listBudgets: ReturnType<typeof vi.fn>; setBudget: ReturnType<typeof vi.fn> };
  let conflict: { show: ReturnType<typeof vi.fn> };
  let fake: ReturnType<typeof fakeHubFactory>;

  beforeEach(() => {
    api = {
      listBudgets: vi.fn().mockResolvedValue([budgetDto('IT-200'), budgetDto('MK-110', { amount: 360000, spent: 160200, reserved: 0, available: 199800, ownerName: 'Ben Marketing' })]),
      setBudget: vi.fn().mockResolvedValue(budgetDto('IT-200', { amount: 950000, available: 316400, version: 4 })),
    };
    conflict = { show: vi.fn().mockResolvedValue(undefined) };
    fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        { provide: BudgetsService, useValue: api },
        { provide: ConflictDialog, useValue: conflict },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
  });
  const store = () => TestBed.inject(BudgetsStore);
  const problem = (status: number, detail: string) => new HttpErrorResponse({ status, error: { detail, title: 'x' } });

  it('loads the budgets of the current fiscal year through the generated client', async () => {
    await store().load();
    expect(api.listBudgets).toHaveBeenCalledWith({ fiscalYear: currentFiscalYear() });
    expect(store().entities().map((b) => b.costCenterCode)).toEqual(['IT-200', 'MK-110']);
    expect(store().isLoaded()).toBe(true);
  });

  it('a failed load sets the error and keeps the rows', async () => {
    await store().load();
    api.listBudgets.mockRejectedValue(new Error('down'));
    store().invalidate('list');
    await vi.waitFor(() => expect(store().error()).toBe('The budgets could not be loaded.'));
    expect(store().entities()).toHaveLength(2);
  });

  it('edit_sends_if_match: the loaded version goes in If-Match and the answer replaces the row', async () => {
    await store().load();
    const saved = await store().save('IT-200', 950000);
    expect(saved).toBe(true);
    expect(api.setBudget).toHaveBeenCalledWith({ costCenter: 'IT-200', fiscalYear: 'FY2027', 'If-Match': '"3"', body: { amount: 950000 } });
    const row = store().entities().find((b) => b.costCenterCode === 'IT-200')!;
    expect(row.amount).toBe(950000);
    expect(row.version).toBe(4);
    expect(store().busyCode()).toBeNull();
  });

  it('edit_422_message_shown: the refusal of the server is kept as the message and the row is unchanged', async () => {
    await store().load();
    api.setBudget.mockRejectedValue(problem(422, 'The budget cannot go below R 633 600,00 (spent plus reserved).'));
    const saved = await store().save('IT-200', 100000);
    expect(saved).toBe(false);
    expect(store().saveError()).toBe('The budget cannot go below R 633 600,00 (spent plus reserved).');
    expect(store().entities().find((b) => b.costCenterCode === 'IT-200')!.amount).toBe(900000);
    expect(conflict.show).not.toHaveBeenCalled();
    store().clearSaveError();
    expect(store().saveError()).toBeNull();
  });

  it('field errors of a 400 without a place of their own are joined into the message', async () => {
    await store().load();
    api.setBudget.mockRejectedValue(new HttpErrorResponse({ status: 400, error: { errors: { amount: ['Must be positive.'], body: ['Bad.'] } } }));
    expect(await store().save('IT-200', -1)).toBe(false);
    expect(store().saveError()).toBe('Must be positive. Bad.');
  });

  it.each([412, 409])('%i opens the conflict dialog, loads the latest budgets and reports no error', async (status) => {
    await store().load();
    api.setBudget.mockRejectedValue(new HttpErrorResponse({ status }));
    api.listBudgets.mockResolvedValue([budgetDto('IT-200', { amount: 920000, version: 5 })]);
    const saved = await store().save('IT-200', 950000);
    expect(saved).toBe(false);
    expect(conflict.show).toHaveBeenCalledTimes(1);
    expect(store().saveError()).toBeNull();
    expect(store().entities()[0].version).toBe(5);
    expect(store().conflicts()).toBe(1);
  });

  it('one write at a time: a double click shares the call, another budget meanwhile is refused', async () => {
    await store().load();
    let finish!: (b: unknown) => void;
    api.setBudget.mockReturnValue(new Promise((resolve) => (finish = resolve)));
    const first = store().save('IT-200', 950000);
    const again = store().save('IT-200', 950000);
    const other = await store().save('MK-110', 400000);
    expect(other).toBe(false);
    expect(store().busyCode()).toBe('IT-200');
    finish(budgetDto('IT-200', { amount: 950000, version: 4 }));
    expect(await first).toBe(true);
    expect(await again).toBe(true);
    expect(api.setBudget).toHaveBeenCalledTimes(1);
  });

  it('a write finishing after reset (logout) changes nothing', async () => {
    await store().load();
    let finish!: (b: unknown) => void;
    api.setBudget.mockReturnValue(new Promise((resolve) => (finish = resolve)));
    const pending = store().save('IT-200', 950000);
    store().reset();
    finish(budgetDto('IT-200', { amount: 950000, version: 4 }));
    await pending;
    expect(store().entities()).toHaveLength(0);
    expect(store().busyCode()).toBeNull();
  });

  it('budget_entity_changed_revalidates: a budget push refreshes the rows in the background', async () => {
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    TestBed.inject(BudgetsStore);
    await store().load();
    api.listBudgets.mockResolvedValue([budgetDto('IT-200', { reserved: 100000, available: 241400, version: 4 })]);
    fake.hubs[0].emit('EntityChanged', { entity: 'budget', id: 'IT-200', requestId: 'r1', eventType: 'BudgetReserved', occurredAt: '2026-10-29T10:00:00Z' });
    await vi.waitFor(() => expect(store().entities()[0].reserved).toBe(100000));
    expect(store().isLoading()).toBe(false);
    live.stop();
  });
});
