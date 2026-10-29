import { computed, inject } from '@angular/core';
import { signalStore, withComputed, withMethods } from '@ngrx/signals';
import { RequestsService } from '../../api/requests/services/requests.service';
import { withPagedList } from '../../core/state/with-paged-list';

const STATUS_PARAM = { open: 'Open', closed: 'Closed' } as const;

export type StatusFilter = 'all' | 'open' | 'closed';

export interface RequestsFilter {
  /** Only the requests I made. */
  mine: boolean;
  status: StatusFilter;
}

/**
 * The purchase requests list, one page at a time (see `withPagedList`: stale-while-revalidate, newest call wins, a push or
 * `revalidateAll()` refreshes the page on screen in the background). Calls go through the generated client.
 */
export const RequestsStore = signalStore(
  { providedIn: 'root' },
  withPagedList(() => {
    const api = inject(RequestsService);
    return {
      entity: 'request',
      trailing: true,
      errorMessage: 'The requests could not be loaded.',
      initialFilter: { mine: false, status: 'all' } as RequestsFilter,
      load: (f: RequestsFilter, page: number, pageSize: number) =>
        api.listRequests({ page, pageSize, ...(f.mine ? { mine: f.mine } : {}), ...(f.status !== 'all' ? { status: STATUS_PARAM[f.status] } : {}) }),
    };
  }),
  withComputed(({ filter }) => ({
    mine: computed(() => filter().mine),
    status: computed(() => filter().status),
  })),
  withMethods((store) => ({
    /** Changes the filters and shows page 1 of the result. Any filter left out stays as it is. */
    setFilters: (filters: Partial<RequestsFilter>) => store.setFilter(filters),
  })),
);
