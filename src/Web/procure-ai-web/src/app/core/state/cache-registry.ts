import { Service, signal } from '@angular/core';

/** What a store gives the registry: forget cached keys, and (optionally) go back to its empty state. */
export interface Clearable {
  clearCache(): void;
  reset?(): void;
  /** Refresh what the store holds in the background (after a reconnect). */
  revalidateAll?(options?: { force?: boolean }): void;
  /** True while a background refresh of this store has failed. */
  revalidateError?(): boolean;
}

/**
 * Every store with `withCache()` registers here. Logout (and later a tenant switch) calls `clearAll()`, so no
 * data of the previous user or tenant survives in memory.
 */
@Service()
export class CacheRegistry {
  private readonly stores = new Set<Clearable>();
  /** Changes when a store is added or removed, so `revalidateErrors()` re-evaluates in a computed or template. */
  private readonly membership = signal(0);

  /** Adds a store. Returns a function that removes it again (a store that is destroyed calls it, so it cannot leak). */
  register(store: Clearable): () => void {
    this.stores.add(store);
    this.membership.update((n) => n + 1);
    return () => {
      this.stores.delete(store);
      this.membership.update((n) => n + 1);
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

  /** True while any registered store reports a failed background refresh (read it in a template: it is reactive). */
  revalidateErrors(): boolean {
    this.membership();
    for (const store of this.stores) {
      if (store.revalidateError?.()) {
        return true;
      }
    }
    return false;
  }

  /** The Retry button: refreshes everything in each store that reports a failure, session-long stores included. */
  retryRevalidation(): void {
    for (const store of this.stores) {
      try {
        if (store.revalidateError?.()) {
          store.revalidateAll?.({ force: true });
        }
      } catch (error) {
        console.error('Retrying a refresh failed', error);
      }
    }
  }
}
