import { Service } from '@angular/core';

/** What a store gives the registry: forget cached keys, and (optionally) go back to its empty state. */
export interface Clearable {
  clearCache(): void;
  reset?(): void;
}

/**
 * Every store with `withCache()` registers here. Logout (and later a tenant switch) calls `clearAll()`, so no
 * data of the previous user or tenant survives in memory.
 */
@Service()
export class CacheRegistry {
  private readonly stores = new Set<Clearable>();

  register(store: Clearable): void {
    this.stores.add(store);
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
}
