import { computed, inject } from '@angular/core';
import { patchState, signalStoreFeature, withComputed, withHooks, withMethods, withProps, withState, withFeature } from '@ngrx/signals';
import { setAllEntities, withEntities } from '@ngrx/signals/entities';
import { AppConfigService } from '../config/app-config';
import { LiveConnection } from '../live/live-connection';
import { Logger } from '../logging/logger';
import { withCache } from './with-cache';
import { withRequestStatus } from './with-request-status';

/** One page of a list, as our APIs return it. */
export interface PagedResult<TItem> {
  items: TItem[];
  page: number;
  pageSize: number;
  total: number;
}

export interface PagedListOptions<TItem, TFilter extends object> {
  /** The entity kind the hub pushes about (`request`, `order` ...); also part of the cache keys (`list:<entity>:<filter>:<page>`). */
  entity: string;
  /** The filter when the store is created (and after `reset()`). */
  initialFilter: TFilter;
  /** Asks the API for one page (1-based). */
  load: (filter: TFilter, page: number, pageSize: number) => Promise<PagedResult<TItem>>;
  /** A push is followed by one more quiet reload (`cache.trailingReloadMs`): for data another service writes from the same event. */
  trailing?: boolean;
  /** Shown as `error` when a load fails, e.g. "The orders could not be loaded." */
  errorMessage: string;
  /** Runs after a page was stored (on screen); `load` shows another page, `patch` sets store-specific state. */
  afterLoad?: (result: PagedResult<TItem>, api: { load(page: number): Promise<void>; patch(state: object): void }) => void;
}

/**
 * The skeleton of a server-paged list store: entities, `page` / `pageSize` / `total` / `filter`, a request status and a cache
 * (see `withCache`). Options may be given as a function, which runs in the injection context (to `inject` the API client).
 *
 * `load(page)` shows one page: only the newest call may change the state; an answer arriving late or after a logout is dropped;
 * a failure sets `error` and keeps the rows; the page on screen is refreshed in the background, a different page or filter loads
 * in the foreground. `setFilter(partial)` changes the filter and shows page 1, `setPage(n)` is `load(n)`, `reload()` refreshes the
 * page on screen in the background. A hub push about `entity` does the same (once more after a while with `trailing`).
 */
export function withPagedList<TItem extends { id: string }, TFilter extends object>(
  optionsOrFactory: PagedListOptions<TItem, TFilter> | (() => PagedListOptions<TItem, TFilter>),
) {
  const resolve = () => (typeof optionsOrFactory === 'function' ? optionsOrFactory() : optionsOrFactory);
  return signalStoreFeature(
    // The options are resolved once per store (the factory may inject services).
    withProps(() => ({ _options: resolve() })),
    withEntities<TItem>(),
    withFeature((store) =>
      withState(() => ({ page: 1, pageSize: inject(AppConfigService).config().paging.pageSize, total: 0, filter: store._options.initialFilter })),
    ),
    withRequestStatus(),
    withComputed(({ total, pageSize }) => ({
      pageCount: computed(() => Math.max(1, Math.ceil(total() / pageSize()))),
    })),
    withCache(),
    withMethods((store) => {
      const options = store._options;
      const log = inject(Logger).withSource(`PagedList(${options.entity})`);
      let latest = 0;
      /** The page the entities hold, and the cache epoch they were loaded in (a cleared cache means they are gone). */
      let shown: { key: string; epoch: number } | null = null;
      /** The page most recently asked for: asking for the same one again shares the running load instead of restarting it. */
      let currentKey: string | null = null;

      const methods = {
        async load(page: number): Promise<void> {
          const pageSize = store.pageSize();
          const filter = store.filter() as TFilter;
          const key = `list:${options.entity}:${JSON.stringify(filter)}:${page}`;
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
                const result = await options.load(filter, page, pageSize);
                if (call !== latest || epoch !== store.cacheEpoch()) {
                  return;
                }
                patchState(store, setAllEntities(result.items as never), { page: result.page, pageSize: result.pageSize, total: result.total });
                shown = { key, epoch };
                store.setLoaded();
                options.afterLoad?.(result, { load: (p) => methods.load(p), patch: (state) => patchState(store, state) });
              } catch (error) {
                if (call === latest && epoch === store.cacheEpoch()) {
                  log.error(`Loading the ${options.entity} list failed`, error);
                  store.setError(error, options.errorMessage);
                }
                throw error; // so the cache does not remember this key as loaded; the caller reads `error`
              }
            });
          } catch {
            // Already recorded in `error`.
          }
        },

        /** Changes the filter (fields left out stay as they are) and shows page 1 of the result. */
        async setFilter(filter: Partial<TFilter>): Promise<void> {
          patchState(store, (s) => ({ filter: { ...(s.filter as TFilter), ...filter } }));
          await methods.load(1);
        },

        setPage(page: number): Promise<void> {
          return methods.load(page);
        },

        /** Refreshes the page on screen in the background (nothing happens if none was loaded). */
        reload(): void {
          store.invalidate('list');
        },
      };
      return methods;
    }),
    withHooks((store) => {
      let off: (() => void) | null = null;
      return {
        onInit() {
          const options = store._options;
          off = inject(LiveConnection).onEntityChanged(options.entity, () => store.invalidate('list', { trailing: options.trailing ?? false }));
        },
        onDestroy() {
          off?.();
        },
      };
    }),
  );
}
