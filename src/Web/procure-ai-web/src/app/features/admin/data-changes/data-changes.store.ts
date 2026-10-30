import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { UserDto } from '../../../api/access/models/user-dto';
import { AccessService } from '../../../api/access/services/access.service';
import { DataChangesService } from '../../../api/audit/services/data-changes.service';
import { Logger } from '../../../core/logging/logger';
import { withPagedList } from '../../../core/state/with-paged-list';

/** The log's filters, as they are in the page URL (`?service=requestsdb&table=...&user=...&outside=true&from=2026-10-01&to=2026-10-30`). */
export interface DataChangesFilter {
  /** The captured database (`requestsdb`); '' = every service. */
  service: string;
  table: string;
  /** A user id; '' = everyone. */
  user: string;
  /** Only changes made outside the app. */
  outside: boolean;
  /** First day, `yyyy-MM-dd` (inclusive); ''. */
  from: string;
  /** Last day, `yyyy-MM-dd` (inclusive); ''. */
  to: string;
}

export const NO_FILTER: DataChangesFilter = { service: '', table: '', user: '', outside: false, from: '', to: '' };

/** Start of a local calendar day (`yyyy-MM-dd`) as an ISO instant; `plusDays` 1 gives the exclusive end of the last day. */
export function startOfDay(day: string, plusDays = 0): string | undefined {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day);
  if (!match) {
    return undefined;
  }
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]) + plusDays);
  return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
}

interface UsersState {
  /** For names and the filter picker; empty when Access could not be asked (ids are shown then). */
  users: UserDto[];
}

/**
 * The data-change log (`admin.system`): every captured change, newest first, filtered (see {@link DataChangesFilter}) and paged.
 * `setFilter` changes the filter and shows page 1. Provide it in the page's `providers`.
 */
export const DataChangesStore = signalStore(
  withState<UsersState>({ users: [] }),
  withPagedList(() => {
    const api = inject(DataChangesService);
    return {
      entity: 'data-change',
      errorMessage: 'The data changes could not be loaded.',
      initialFilter: NO_FILTER,
      load: (f: DataChangesFilter, page: number, pageSize: number) =>
        api.searchDataChanges({
          page,
          pageSize,
          ...(f.service ? { database: f.service } : {}),
          ...(f.table ? { table: f.table } : {}),
          ...(f.user ? { userId: f.user } : {}),
          ...(f.outside ? { outsideOnly: true } : {}),
          ...(startOfDay(f.from) ? { from: startOfDay(f.from) } : {}),
          ...(startOfDay(f.to, 1) ? { to: startOfDay(f.to, 1) } : {}),
        }),
    };
  }),
  withComputed((store) => ({
    /** user id to display name. */
    names: computed(() => new Map(store.users().map((u) => [u.userId, u.displayName]))),
  })),
  withMethods((store) => {
    const access = inject(AccessService);
    const log = inject(Logger).withSource('DataChangesStore');
    let requested = false;
    return {
      /** Reads the users once for names; a failure leaves the ids. */
      async loadUsers(): Promise<void> {
        if (requested) {
          return;
        }
        requested = true;
        try {
          patchState(store, { users: await access.listUsers() });
        } catch (error) {
          log.warn('Reading the users for names failed', error);
        }
      },
    };
  }),
);

export type DataChangesStore = InstanceType<typeof DataChangesStore>;
