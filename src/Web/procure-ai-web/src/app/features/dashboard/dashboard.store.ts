import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { ActivityDto } from '../../api/audit/models/activity-dto';
import { AuditService } from '../../api/audit/services/audit.service';
import { BudgetDto } from '../../api/budgets/models/budget-dto';
import { BudgetsService } from '../../api/budgets/services/budgets.service';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { RequestsService } from '../../api/requests/services/requests.service';
import { AuthStore } from '../../core/auth/auth.store';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { ReferenceDataStore } from '../../core/reference/reference-data.store';
import { withCache } from '../../core/state/with-cache';
import { errorMessage } from '../../core/state/with-request-status';
import { ApprovalsStore } from '../approvals/approvals.store';
import { AUDIT_LAG_MS } from '../requests/request-detail.store';

const ACTIVITY_LIMIT = 10;
/** Users with budget access but no cost center of their own (finance) get an overview of this many budgets. */
const OVERVIEW_BUDGETS = 3;

export interface BudgetTile {
  code: string;
  amount: number;
  spent: number;
  reserved: number;
  available: number;
  /** Whole percent of the budget already spent, and of what is reserved on top (the two never exceed 100 together). */
  spentPercent: number;
  reservedPercent: number;
}

interface DashboardState {
  myOpen: number | null;
  mismatched: number | null;
  budgets: BudgetDto[];
  activity: ActivityDto[];
  /** Tiles that could not be loaded, by tile key, in words. */
  errors: Record<string, string>;
}

/**
 * The company fiscal year the instant falls in: 1 March to the end of February in South African time, named after the
 * year it ends in (1 March 2026 to 28 February 2027 is FY2027), like Budgets does.
 */
export function currentFiscalYear(at: Date = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-ZA', { timeZone: 'Africa/Johannesburg', year: 'numeric', month: 'numeric' }).formatToParts(at);
  const year = Number(parts.find((p) => p.type === 'year')?.value);
  const month = Number(parts.find((p) => p.type === 'month')?.value);
  return `FY${month >= 3 ? year + 1 : year}`;
}

const percent = (part: number, whole: number) => (whole > 0 ? Math.round((part / whole) * 100) : 0);

/**
 * The dashboard's numbers. Each tile is its own cached load (so one failing tile leaves the others alone) and exists
 * only for users who hold the permission: the waiting-approvals tile shares `ApprovalsStore` with the inbox and the
 * header. Pushes refresh the tile they concern; recent activity refreshes on every push and once more after the
 * audit lag (Audit stores events a moment after the push).
 */
export const DashboardStore = signalStore(
  { providedIn: 'root' },
  withState<DashboardState>({ myOpen: null, mismatched: null, budgets: [], activity: [], errors: {} }),
  withComputed((store) => {
    const auth = inject(AuthStore);
    const approvals = inject(ApprovalsStore);
    const refs = inject(ReferenceDataStore);
    return {
      /** Which tiles this user gets. */
      tiles: computed(() => ({
        approvals: auth.hasPermission('requests.approve'),
        myOpen: auth.hasPermission('requests.read'),
        budgets: auth.hasPermission('budgets.read'),
        mismatched: auth.hasPermission('invoices.read'),
        activity: auth.hasPermission('requests.read'),
      })),
      approvalsTotal: computed(() => approvals.total()),
      /** The earliest time a waiting step escalates (the inbox is ordered by it), or null. */
      soonestEscalation: computed(() => approvals.soonestDueAt()),
      /** The waiting-approvals tile could not be loaded (the inbox store's error), so it must not show 0. */
      approvalsError: computed(() => approvals.error()),
      budgetTiles: computed<BudgetTile[]>(() => {
        const mine = new Set(refs.costCenters().map((c) => c.code));
        const wanted = mine.size > 0 ? store.budgets().filter((b) => mine.has(b.costCenterCode)) : store.budgets().slice(0, OVERVIEW_BUDGETS);
        return wanted.map((b) => {
          const spentPercent = Math.min(100, percent(b.spent, b.amount));
          return {
            code: b.costCenterCode,
            amount: b.amount,
            spent: b.spent,
            reserved: b.reserved,
            available: b.available,
            spentPercent,
            reservedPercent: Math.min(100 - spentPercent, percent(b.reserved, b.amount)),
          };
        });
      }),
    };
  }),
  withCache(),
  withMethods((store) => {
    const requests = inject(RequestsService);
    const invoices = inject(InvoicesService);
    const budgets = inject(BudgetsService);
    const audit = inject(AuditService);
    const refs = inject(ReferenceDataStore);
    const approvals = inject(ApprovalsStore);
    const log = inject(Logger).withSource('DashboardStore');
    let trailing: ReturnType<typeof setTimeout> | null = null;

    /** One tile: cached load; a failure is recorded for the tile and does not stop the others. */
    const tile = async (key: string, read: () => Promise<Partial<DashboardState>>): Promise<void> => {
      const epoch = store.cacheEpoch();
      try {
        await store.cacheLoad(key, async () => {
          const value = await read();
          if (epoch === store.cacheEpoch()) {
            patchState(store, (s) => ({ ...value, errors: Object.fromEntries(Object.entries(s.errors).filter(([k]) => k !== key)) }));
          }
        });
      } catch (error) {
        if (epoch === store.cacheEpoch()) {
          log.warn(`Loading the "${key}" tile failed`, error);
          patchState(store, (s) => ({ errors: { ...s.errors, [key]: errorMessage(error, 'This could not be loaded.') } }));
        }
      }
    };

    const reads = {
      myOpen: () => tile('myOpen', async () => ({ myOpen: (await requests.listRequests({ mine: true, status: 'Open', page: 1, pageSize: 1 })).total })),
      mismatched: () => tile('mismatched', async () => ({ mismatched: (await invoices.listInvoices({ status: 'Mismatched', page: 1, pageSize: 1 })).total })),
      budgets: () =>
        tile('budgets', async () => {
          await refs.loadMe();
          return { budgets: await budgets.listBudgets({ fiscalYear: currentFiscalYear() }) };
        }),
      activity: () => tile('activity', async () => ({ activity: await audit.getActivity({ limit: ACTIVITY_LIMIT }) })),
    };

    return {
      /** Loads the tiles this user may see (fresh ones are skipped). Never throws. */
      async load(): Promise<void> {
        const tiles = store.tiles();
        await Promise.all([
          tiles.approvals ? approvals.refresh() : undefined,
          tiles.myOpen ? reads.myOpen() : undefined,
          tiles.budgets ? reads.budgets() : undefined,
          tiles.mismatched ? reads.mismatched() : undefined,
          tiles.activity ? reads.activity() : undefined,
        ]);
      },

      /** Loads one tile again now (its "Try again"). */
      retry(key: 'approvals' | 'myOpen' | 'mismatched' | 'budgets' | 'activity'): void {
        if (key === 'approvals') {
          void approvals.refresh();
          return;
        }
        store.invalidate(key);
        void reads[key]();
      },

      /** A push: refresh what it concerns. */
      onChange(entity: string): void {
        if (entity === 'request') {
          store.invalidate('myOpen');
        } else if (entity === 'budget') {
          store.invalidate('budgets');
        } else if (entity === 'invoice') {
          store.invalidate('mismatched');
        }
        store.invalidate('activity');
        if (trailing) {
          clearTimeout(trailing);
        }
        trailing = setTimeout(() => {
          trailing = null;
          store.invalidate('activity');
        }, AUDIT_LAG_MS);
      },

      stop(): void {
        if (trailing) {
          clearTimeout(trailing);
          trailing = null;
        }
      },
    };
  }),
  withHooks((store) => {
    const live = inject(LiveConnection);
    let release: (() => void)[] = [];
    return {
      onInit() {
        release = ['request', 'approval', 'budget', 'order', 'invoice'].map((entity) => live.onEntityChanged(entity, () => store.onChange(entity)));
      },
      onDestroy() {
        release.forEach((off) => off());
        store.stop();
      },
    };
  }),
);
