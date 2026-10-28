import { Injector, runInInjectionContext, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { createBudgetCheck } from './budget-check';
import { createApproversPreview } from './approvers-preview';

describe('debounced lookups', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  const inContext = <T>(fn: () => T) => runInInjectionContext(TestBed.inject(Injector), fn);

  it('budget_check_debounced_and_shows_over_budget', async () => {
    const checkBudget = vi.fn().mockResolvedValue({ available: 50000, fiscalYear: '2026', fits: false });
    const costCenter = signal('IT-200');
    const amount = signal(0);
    const check = inContext(() => createBudgetCheck(costCenter, amount, checkBudget));
    TestBed.tick();
    expect(check.state().kind).toBe('idle');

    amount.set(86000);
    TestBed.tick();
    amount.set(86940); // typing on: the timer restarts
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(399);
    expect(checkBudget).not.toHaveBeenCalled();
    expect(check.state().kind).toBe('checking');

    await vi.advanceTimersByTimeAsync(1);

    expect(checkBudget).toHaveBeenCalledOnce();
    expect(checkBudget).toHaveBeenCalledWith({ costCenter: 'IT-200', amount: 86940 });
    expect(check.state()).toEqual({ kind: 'over', available: 50000, overBy: 36940 });
  });

  it('shows within budget, and an unreachable service as unknown rather than blocking', async () => {
    const checkBudget = vi.fn().mockResolvedValueOnce({ available: 266400, fiscalYear: '2026', fits: true }).mockRejectedValueOnce(new Error('down'));
    const amount = signal(100);
    const check = inContext(() => createBudgetCheck(signal('IT-200'), amount, checkBudget));
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(400);
    expect(check.state()).toEqual({ kind: 'within', available: 266400 });

    amount.set(200);
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(400);
    expect(check.state().kind).toBe('unknown');
  });

  it('an answer for an older amount never replaces a newer one', async () => {
    let first!: (v: unknown) => void;
    const checkBudget = vi.fn()
      .mockImplementationOnce(() => new Promise((resolve) => (first = resolve)))
      .mockResolvedValueOnce({ available: 10, fiscalYear: '2026', fits: true });
    const amount = signal(1);
    const check = inContext(() => createBudgetCheck(signal('IT-200'), amount, checkBudget));
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(400);
    amount.set(2);
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(400);

    first({ available: 999, fiscalYear: '2026', fits: false });
    await vi.advanceTimersByTimeAsync(0);

    expect(check.state()).toEqual({ kind: 'within', available: 10 });
  });

  it('approvers preview lists numbered steps, and says when nobody can approve', async () => {
    const previewApproval = vi.fn()
      .mockResolvedValueOnce({ possible: true, reason: null, steps: [{ stepNo: 1, level: 'Manager', approverNames: ['Maria'] }, { stepNo: 2, level: 'Department head', approverNames: ['Ben', 'Ana'] }] })
      .mockResolvedValueOnce({ possible: false, reason: 'No approver can approve R 900 000,00.', steps: [] });
    const amount = signal(86940);
    const preview = inContext(() => createApproversPreview(signal('IT-200'), amount, previewApproval));
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(400);

    expect(preview.state()).toEqual({
      kind: 'steps',
      steps: [{ stepNo: 1, level: 'Manager', approverNames: ['Maria'] }, { stepNo: 2, level: 'Department head', approverNames: ['Ben', 'Ana'] }],
    });

    amount.set(900000);
    TestBed.tick();
    await vi.advanceTimersByTimeAsync(400);
    expect(preview.state()).toEqual({ kind: 'nobody', reason: 'No approver can approve R 900 000,00.' });
  });
});
