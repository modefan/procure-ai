import { Service } from '@angular/core';

/** What a store gives the registry: forget cached keys, and (optionally) go back to its empty state. */
export interface Clearable {
  clearCache(): void;
  reset?(): void;
  /** Refresh what the store holds in the background (after a reconnect). */
  revalidateAll?(): void;
}

/**
 * Every store with `withCache()` registers here. Logout (and later a tenant switch) calls `clearAll()`, so no
 * data of the previous user or tenant survives in memory.
 */
@Service()
export class CacheRegistry {
  private readonly stores = new Set<Clearable>();

  /** Adds a store. Returns a function that removes it again (a store that is destroyed calls it, so it cannot leak). */
  register(store: Clearable): () => void {
    this.stores.add(store);
    return () => {
      this.stores.delete(store);
    };
  }

  /** Clears the cache of every registered store and resets its state. One failing store does not stop the rest. */
  clearAll(): void {
    for (const store of this.stores) {
      try {
        store.clearCache();
        store.reset?.();
      } catch (error) {
        console.error('Clearing a store failed', error);
      }
    }
  }

  /** Asks every registered store to refresh what it holds (after a connect or reconnect). One failing store does not stop the rest. */
  revalidateAll(): void {
    for (const store of this.stores) {
      try {
        store.revalidateAll?.();
      } catch (error) {
        console.error('Revalidating a store failed', error);
      }
    }
  }
}
