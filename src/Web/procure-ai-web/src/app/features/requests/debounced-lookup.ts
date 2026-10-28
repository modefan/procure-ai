import { computed, DestroyRef, effect, inject, signal, Signal } from '@angular/core';

export type LookupState<T> = { status: 'idle' } | { status: 'loading' } | { status: 'loaded'; value: T } | { status: 'error' };

/**
 * Calls `fetch` for the current `params` once they have stayed the same for `delayMs` (typing restarts the wait).
 * `params()` returning null means "nothing to ask" (idle). A late answer for older params is dropped.
 * Call in an injection context.
 */
export function debouncedLookup<P, T>(params: () => P | null, fetch: (params: P) => Promise<T>, delayMs = 400): Signal<LookupState<T>> {
  const state = signal<LookupState<T>>({ status: 'idle' });
  // Equal by value, so recomputing the same numbers does not restart the timer.
  const key = computed(params, { equal: (a, b) => JSON.stringify(a) === JSON.stringify(b) });
  let timer: ReturnType<typeof setTimeout> | undefined;
  let call = 0;

  effect(() => {
    const current = key();
    clearTimeout(timer);
    const mine = ++call;
    if (current === null) {
      state.set({ status: 'idle' });
      return;
    }
    state.set({ status: 'loading' });
    timer = setTimeout(() => {
      fetch(current).then(
        (value) => mine === call && state.set({ status: 'loaded', value }),
        () => mine === call && state.set({ status: 'error' }),
      );
    }, delayMs);
  });
  inject(DestroyRef).onDestroy(() => {
    call++;
    clearTimeout(timer);
  });
  return state.asReadonly();
}
