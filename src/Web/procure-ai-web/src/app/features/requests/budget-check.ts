import { computed, inject, Signal } from '@angular/core';
import { BudgetCheckDto } from '../../api/budgets/models/budget-check-dto';
import { BudgetsService } from '../../api/budgets/services/budgets.service';
import { debouncedLookup } from './debounced-lookup';

export type BudgetCheckState =
  | { kind: 'idle' }
  | { kind: 'checking' }
  | { kind: 'within'; available: number }
  | { kind: 'over'; available: number; overBy: number }
  /** Budgets could not be asked: shown as "could not check", never blocks saving (the server decides at submit). */
  | { kind: 'unknown' };

type Check = (params: { costCenter: string; amount: number }) => Promise<BudgetCheckDto>;

/** The live budget check of the form: asks Budgets 400 ms after the cost center or total last changed. */
export function createBudgetCheck(
  costCenter: () => string,
  amount: () => number,
  check?: Check,
): { state: Signal<BudgetCheckState> } {
  const api = check ? null : inject(BudgetsService); // resolved now: inject() is not allowed later, when the timer fires
  const raw = debouncedLookup(() => (costCenter() !== '' && amount() > 0 ? { costCenter: costCenter(), amount: amount() } : null), check ?? ((p) => api!.checkBudget(p)));
  const state = computed<BudgetCheckState>(() => {
    const s = raw();
    switch (s.status) {
      case 'idle':
        return { kind: 'idle' };
      case 'loading':
        return { kind: 'checking' };
      case 'error':
        return { kind: 'unknown' };
      case 'loaded':
        return s.value.fits
          ? { kind: 'within', available: s.value.available }
          : { kind: 'over', available: s.value.available, overBy: Math.round(amount() * 100 - s.value.available * 100) / 100 };
    }
  });
  return { state };
}
