import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { setAllEntities, withEntities } from '@ngrx/signals/entities';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsService } from '../../api/requests/services/requests.service';
import { AppConfigService } from '../../core/config/app-config';
import { Logger } from '../../core/logging/logger';
import { withCache } from '../../core/state/with-cache';
import { withRequestStatus } from '../../core/state/with-request-status';

export interface RequestsState {
  page: number;
  pageSize: number;
  total: number;
}

/**
 * The purchase requests list, one page at a time. Calls go through the generated client. Stale-while-revalidate:
 * asking again for the page on screen within the stale time does nothing; after it, the page refreshes in the
 * background and the rows stay. The store holds one page, so moving to another page loads it in the foreground.
 * `revalidateAll()` (from a SignalR push or a reconnect) refreshes the page on screen.
 */
export const RequestsStore = signalStore(
  { providedIn: 'root' },
  withEntities<RequestDto>(),
  withState<RequestsState>(() => ({ page: 1, pageSize: inject(AppConfigService).config().paging.pageSize, total: 0 })),
  withRequestStatus(),
  withComputed(({ total, pageSize }) => ({
    pageCount: computed(() => Math.max(1, Math.ceil(total() / pageSize()))),
  })),
  withCache(),
  withMethods((store) => {
    const api = inject(RequestsService);
    const log = inject(Logger).withSource('RequestsStore');
    let latest = 0;
    /** The page the entities hold, and the cache epoch they were loaded in (a cleared cache means they are gone). */
    let shown: { key: string; epoch: number } | null = null;

    return {
      /**
       * Shows one page (1-based). Only the newest call may change the state; an older answer arriving late, or one
       * arriving after the cache was cleared (logout), is dropped. A failure sets `error` and keeps the rows.
       */
      async load(page: number): Promise<void> {
        const pageSize = store.pageSize();
        const key = `list:${page}:${pageSize}`;
        const onScreen = () => shown !== null && shown.key === key && shown.epoch === store.cacheEpoch();
        if (!onScreen()) {
          store.clearCache(); // one page is held, so what was cached for other pages is of no use
        }
        try {
          await store.cacheLoad(key, async () => {
            const call = ++latest;
            const epoch = store.cacheEpoch();
            if (!onScreen()) {
              store.setLoading();
            }
            try {
              const result = await api.listRequests({ page, pageSize });
              if (call !== latest || epoch !== store.cacheEpoch()) {
                return;
              }
              patchState(store, setAllEntities(result.items), { page: result.page, pageSize: result.pageSize, total: result.total });
              shown = { key, epoch };
              store.setLoaded();
            } catch (error) {
              if (call === latest && epoch === store.cacheEpoch()) {
                log.error('Loading the requests failed', error);
                store.setError(error, 'The requests could not be loaded.');
              }
              throw error; // so the cache does not remember this key as loaded; the caller reads `error`
            }
          });
        } catch {
          // Already recorded in `error`.
        }
      },
    };
  }),
);
