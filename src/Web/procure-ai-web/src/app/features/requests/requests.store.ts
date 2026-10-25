import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { setAllEntities, withEntities } from '@ngrx/signals/entities';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsService } from '../../api/requests/services/requests.service';
import { AppConfigService } from '../../core/config/app-config';
import { Logger } from '../../core/logging/logger';

export interface RequestsState {
  page: number;
  pageSize: number;
  total: number;
  loading: boolean;
  /** A message for the user when the last load failed; null otherwise. */
  error: string | null;
}

/**
 * The purchase requests list, one page at a time. Calls go through the generated client. Caching rules
 * (stale-while-revalidate, SignalR invalidation) arrive with the caching features of the later plans.
 */
export const RequestsStore = signalStore(
  { providedIn: 'root' },
  withEntities<RequestDto>(),
  withState<RequestsState>(() => ({ page: 1, pageSize: inject(AppConfigService).config().paging.pageSize, total: 0, loading: false, error: null })),
  withComputed(({ total, pageSize }) => ({
    pageCount: computed(() => Math.max(1, Math.ceil(total() / pageSize()))),
  })),
  withMethods((store) => {
    const api = inject(RequestsService);
    const log = inject(Logger).withSource('RequestsStore');
    let latest = 0;

    return {
      /** Loads one page (1-based). Only the newest call may change the state; an older answer arriving late is dropped. */
      async load(page: number): Promise<void> {
        const call = ++latest;
        patchState(store, { loading: true, error: null });
        try {
          const result = await api.listRequests({ page, pageSize: store.pageSize() });
          if (call !== latest) {
            return;
          }
          patchState(store, setAllEntities(result.items), {
            page: result.page,
            pageSize: result.pageSize,
            total: result.total,
            loading: false,
          });
        } catch (error) {
          if (call !== latest) {
            return;
          }
          log.error('Loading the requests failed', error);
          patchState(store, { loading: false, error: 'The requests could not be loaded.' });
        }
      },
    };
  }),
);
