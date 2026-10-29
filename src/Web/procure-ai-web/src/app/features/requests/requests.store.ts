import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { setAllEntities, withEntities } from '@ngrx/signals/entities';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsService } from '../../api/requests/services/requests.service';
import { AppConfigService } from '../../core/config/app-config';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { withCache } from '../../core/state/with-cache';
import { withRequestStatus } from '../../core/state/with-request-status';

const STATUS_PARAM = { open: 'Open', closed: 'Closed' } as const;

export type StatusFilter = 'all' | 'open' | 'closed';

export interface RequestsState {
  /** Only the requests I made. */
  mine: boolean;
  status: StatusFilter;
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
  withState<RequestsState>(() => ({ mine: false, status: 'all' as StatusFilter, page: 1, pageSize: inject(AppConfigService).config().paging.pageSize, total: 0 })),
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
    /** The page most recently asked for: asking for the same one again shares the running load instead of restarting it. */
    let currentKey: string | null = null;

    const methods = {
      /**
       * Shows one page (1-based). Only the newest call may change the state; an older answer arriving late, or one
       * arriving after the cache was cleared (logout), is dropped. A failure sets `error` and keeps the rows.
       */
      async load(page: number): Promise<void> {
        const pageSize = store.pageSize();
        const { mine, status } = { mine: store.mine(), status: store.status() };
        const key = `list:${page}:${pageSize}:${mine ? 'mine' : 'all'}:${status}`;
        const onScreen = () => shown !== null && shown.key === key && shown.epoch === store.cacheEpoch();
        if (currentKey !== key) {
          store.clearCache(); // one page is held, so what was cached for other pages is of no use
          currentKey = key;
        }
        try {
          await store.cacheLoad(key, async () => {
            const call = ++latest;
            const epoch = store.cacheEpoch();
            if (!onScreen()) {
              store.setLoading();
            }
            try {
              const result = await api.listRequests({ page, pageSize, ...(mine ? { mine } : {}), ...(status !== 'all' ? { status: STATUS_PARAM[status] } : {}) });
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

      /** Changes the filters and shows page 1 of the result. Any filter left out stays as it is. */
      async setFilters(filters: { mine?: boolean; status?: StatusFilter }): Promise<void> {
        patchState(store, filters);
        await methods.load(1);
      },
    };
    return methods;
  }),
  withHooks((store) => {
    let off: (() => void) | null = null;
    return {
      onInit() {
        // A push about a request refreshes the page on screen in the background (nothing happens if none was loaded).
        off = inject(LiveConnection).onEntityChanged('request', () => store.invalidate('list', { trailing: true }));
      },
      onDestroy() {
        off?.();
      },
    };
  }),
);
