import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { setAllEntities, setEntity, withEntities } from '@ngrx/signals/entities';
import { BudgetDto } from '../../api/budgets/models/budget-dto';
import { BudgetsService } from '../../api/budgets/services/budgets.service';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { withCache } from '../../core/state/with-cache';
import { withRequestStatus } from '../../core/state/with-request-status';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { currentFiscalYear } from '../dashboard/dashboard.store';
import { isConflict, problemOf } from '../../core/http/problem-fields';

interface BudgetsState {
  /** The cost center whose amount is being saved; null when no write runs. */
  busyCode: string | null;
  /** The server's refusal of the last save (422 below spent plus reserved, 400 ...), in words. */
  saveError: string | null;
  /** Counts the saves that lost to someone else (412), so an open dialog knows its figures were just refreshed. */
  conflicts: number;
}


/**
 * The cost center budgets of the current fiscal year, as the API scopes them: finance and admins see every cost center,
 * a requester only their own (the server decides, nothing is filtered here). Stale-while-revalidate like the other
 * lists; a `budget` push or a reconnect refreshes the rows on screen in the background. Saving an amount
 * (`budgets.manage`) waits for the server, sends `If-Match` with the loaded version, and runs one at a time. A 412 opens the
 * conflict dialog and loads the latest budgets; a 422 or 400 leaves the server's message in `saveError`.
 */
export const BudgetsStore = signalStore(
  { providedIn: 'root' },
  withEntities<BudgetDto>(),
  withState<BudgetsState>({ busyCode: null, saveError: null, conflicts: 0 }),
  withRequestStatus(),
  withCache(),
  withMethods((store) => {
    const api = inject(BudgetsService);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('BudgetsStore');
    let latest = 0;
    let shown: number | null = null;
    let working: Promise<boolean> | null = null;
    let workingCode: string | null = null;
    let writes = 0;

    /** Reads the list. Only the newest call may change the state; a failure sets `error` and keeps the rows. */
    const fetchList = async (): Promise<void> => {
      const call = ++latest;
      const epoch = store.cacheEpoch();
      if (shown !== epoch) {
        store.setLoading();
      }
      try {
        const budgets = await api.listBudgets({ fiscalYear: currentFiscalYear() });
        if (call !== latest || epoch !== store.cacheEpoch()) {
          return;
        }
        patchState(store, setAllEntities(budgets, { selectId: (b: BudgetDto) => b.costCenterCode }));
        shown = epoch;
        store.setLoaded();
      } catch (error) {
        if (call === latest && epoch === store.cacheEpoch()) {
          log.error('Loading the budgets failed', error);
          store.setError(error, 'The budgets could not be loaded.');
        }
        throw error;
      }
    };

    return {
      /** Shows the budgets. Does nothing while what is shown is fresh. */
      async load(): Promise<void> {
        try {
          await store.cacheLoad('list', fetchList);
        } catch {
          // Already recorded in `error`.
        }
      },

      clearSaveError(): void {
        patchState(store, { saveError: null });
      },

      /** Sets the budget of one cost center. Resolves true when the server accepted it. */
      save(costCenterCode: string, amount: number): Promise<boolean> {
        const budget = store.entityMap()[costCenterCode];
        if (!budget) {
          return Promise.resolve(false);
        }
        if (working) {
          // A double click shares the running call; another cost center while one runs is refused.
          return workingCode === costCenterCode ? working : Promise.resolve(false);
        }
        const epoch = store.cacheEpoch();
        const mine = ++writes;
        workingCode = costCenterCode;
        patchState(store, { busyCode: costCenterCode, saveError: null });
        working = (async () => {
          try {
            const updated = await api.setBudget({ costCenter: budget.costCenterCode, fiscalYear: budget.fiscalYear, 'If-Match': `"${budget.version}"`, body: { amount } });
            if (epoch === store.cacheEpoch()) {
              patchState(store, setEntity(updated, { selectId: (b: BudgetDto) => b.costCenterCode }));
            }
            return epoch === store.cacheEpoch();
          } catch (error) {
            if (epoch !== store.cacheEpoch()) {
              return false;
            }
            if (isConflict(error)) {
              await conflict.show('Someone else just changed this budget. Showing the latest version.');
              await fetchList().catch(() => undefined);
              patchState(store, (s) => ({ saveError: null, conflicts: s.conflicts + 1 }));
            } else {
              log.warn('Saving the budget failed', error);
              const problem = problemOf(error);
              const fields = Object.values(problem.fields).flat().join(' ');
              patchState(store, { saveError: problem.general ?? (fields || 'The budget could not be saved. Please try again.') });
            }
            return false;
          } finally {
            // A logout while the call ran already reset the state; only free the guard if no newer write took it.
            if (mine === writes) {
              working = null;
              workingCode = null;
              if (epoch === store.cacheEpoch()) {
                patchState(store, { busyCode: null });
              }
            }
          }
        })();
        return working;
      },
    };
  }),
  withHooks((store) => {
    let off: (() => void) | null = null;
    return {
      onInit() {
        off = inject(LiveConnection).onEntityChanged('budget', () => store.invalidate('list'));
      },
      onDestroy() {
        off?.();
      },
    };
  }),
);
