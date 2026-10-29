import { inject } from '@angular/core';
import { getState, patchState, signalStoreFeature, type, withHooks, withMethods, withProps } from '@ngrx/signals';
import { AppConfigService } from '../config/app-config';
import { Logger } from '../logging/logger';
import { CacheRegistry } from './cache-registry';
import { createTrailingReload, type TrailingReload } from './trailing-reload';

export interface CacheOptions {
  /** How long a loaded key counts as fresh, in ms. Default `cache.staleMs` of config.json (30 000); `Infinity` for reference data. */
  staleMs?: number;
}

const MAX_KEYS = 50;

interface Entry {
  loader: () => Promise<void>;
  /** When the last successful load finished; null until one has. */
  loadedAt: number | null;
  inFlight: Promise<void> | null;
  /** Invalidated while a load was running: run the loader again when it ends. */
  rerun: boolean;
}

/**
 * Stale-while-revalidate for a store, in memory only (never localStorage).
 *
 * `cacheLoad(key, loader)`: a fresh key does nothing; a stale or new key runs the loader (the store keeps showing
 * what it has while the loader runs, so the loader should not set a loading state unless the screen is empty); the
 * same key already loading shares the running promise. A failed loader is not remembered as loaded.
 * `invalidate(match)` re-runs the loaders of matching keys now, in the background; a string matches a key that equals
 * it or a child of it (`list` matches `list:1`; `list:1` does not match `list:10`). A failing background re-run is
 * logged (warn), not thrown; with `{ trailing: true }` it runs again `cache.trailingReloadMs` after the last call (see
 * `createTrailingReload`). `revalidateAll()` does that for every key, except in a session-long store
 * (`staleMs: Infinity`, reference data) unless called with `{ force: true }`. `clearCache()` forgets everything;
 * `reset()` also puts the store's state back to what it was when the store was created (taken in `onInit`, so the
 * order of features does not matter). At most 50 keys are kept (least recently used dropped).
 * `cacheEpoch()` changes whenever the cache is cleared, so a loader can drop an answer that arrives after a logout.
 * The store registers with `CacheRegistry`, which logout clears.
 */
export function withCache(options: CacheOptions = {}) {

  return signalStoreFeature(
    { state: type<object>() },
    // Internal: where `onInit` leaves the state snapshot for `reset()` and the registry handle for `onDestroy`.
    withProps(() => ({ _cacheInternals: { initialState: null as object | null, deregister: null as (() => void) | null } })),
    withMethods((store) => {
      const log = inject(Logger).withSource('withCache');
      const config = inject(AppConfigService);
      const staleMs = options.staleMs ?? config.config().cache.staleMs;
      const trailers = new Map<string, TrailingReload>();
      const entries = new Map<string, Entry>();
      let epoch = 0;

      const isFresh = (entry: Entry) => entry.loadedAt !== null && Date.now() - entry.loadedAt < staleMs;

      /** Marks a key as most recently used and drops the oldest keys beyond the limit. */
      const touch = (key: string, entry: Entry) => {
        entries.delete(key);
        entries.set(key, entry);
        while (entries.size > MAX_KEYS) {
          entries.delete(entries.keys().next().value as string);
        }
      };

      const run = (key: string, entry: Entry): Promise<void> => {
        const myEpoch = epoch;
        let promise!: Promise<void>;
        promise = (async () => {
          try {
            await entry.loader();
            if (myEpoch === epoch) {
              entry.loadedAt = Date.now();
            }
          } catch (error) {
            if (myEpoch === epoch) {
              entry.loadedAt = null;
            }
            throw error;
          } finally {
            if (entry.inFlight === promise) {
              entry.inFlight = null;
            }
            if (myEpoch === epoch && entry.rerun) {
              entry.rerun = false;
              void run(key, entry).catch((error: unknown) => log.warn(`Background refresh of "${key}" failed`, error));
            }
          }
        })();
        entry.inFlight = promise;
        return promise;
      };

      const matches = (match: string | ((key: string) => boolean)) =>
        typeof match === 'string' ? (key: string) => key === match || key.startsWith(`${match}:`) : match;

      const methods = {
        cacheLoad(key: string, loader: () => Promise<void>): Promise<void> {
          const existing = entries.get(key);
          if (existing?.inFlight) {
            touch(key, existing);
            return existing.inFlight;
          }
          if (existing && isFresh(existing)) {
            touch(key, existing);
            return Promise.resolve();
          }
          const entry: Entry = existing ?? { loader, loadedAt: null, inFlight: null, rerun: false };
          entry.loader = loader;
          touch(key, entry);
          return run(key, entry);
        },

        /**
         * Re-runs the loaders of the matching keys now. With `trailing`, once more `cache.trailingReloadMs` after the last call:
         * for a push about data that another service writes from the same event, which can arrive before the write is done.
         */
        invalidate(match: string | ((key: string) => boolean), invalidateOptions: { trailing?: boolean } = {}): void {
          if (invalidateOptions.trailing) {
            const id = typeof match === 'string' ? match : '*';
            let trailer = trailers.get(id);
            if (!trailer) {
              trailer = createTrailingReload(() => methods.invalidate(match), () => config.config().cache.trailingReloadMs);
              trailers.set(id, trailer);
            }
            trailer.schedule();
          }
          const test = matches(match);
          for (const [key, entry] of [...entries]) {
            if (!test(key)) {
              continue;
            }
            if (entry.inFlight) {
              entry.rerun = true;
            } else {
              void run(key, entry).catch((error: unknown) => log.warn(`Background refresh of "${key}" failed`, error));
            }
          }
        },

        /** Re-runs every loader in the background. A session-long store (`staleMs: Infinity`) is skipped unless `force`. */
        revalidateAll(options: { force?: boolean } = {}): void {
          if (staleMs === Infinity && !options.force) {
            return;
          }
          methods.invalidate(() => true);
        },

        clearCache(): void {
          trailers.forEach((trailer) => trailer.cancel());
          trailers.clear();
          entries.clear();
          epoch++;
        },

        /** Changes every time the cache is cleared. */
        cacheEpoch(): number {
          return epoch;
        },

        /** Clears the cache and puts the state back to what it was when the store was created. */
        reset(): void {
          methods.clearCache();
          patchState(store as never, structuredClone(store._cacheInternals.initialState ?? {}) as never);
        },
      };
      return methods;
    }),
    withHooks({
      onInit(store) {
        store._cacheInternals.initialState = structuredClone(getState(store));
        store._cacheInternals.deregister = inject(CacheRegistry).register(store);
      },
      onDestroy(store) {
        store.clearCache();
        store._cacheInternals.deregister?.();
      },
    }),
  );
}
