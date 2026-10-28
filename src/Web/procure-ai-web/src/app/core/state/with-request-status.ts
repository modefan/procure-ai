import { HttpErrorResponse } from '@angular/common/http';
import { computed } from '@angular/core';
import { patchState, signalStoreFeature, withComputed, withMethods, withState } from '@ngrx/signals';

export type RequestStatus = 'idle' | 'loading' | 'loaded' | { error: string };

const GENERIC_ERROR = 'Something went wrong. Please try again.';

/**
 * The message to show for a failed call: the `detail` (else the `title`) of a ProblemDetails body from our APIs.
 * Anything else (network errors, exceptions) gets the fallback, never a raw exception message.
 */
export function errorMessage(error: unknown, fallback: string = GENERIC_ERROR): string {
  if (error instanceof HttpErrorResponse && error.error && typeof error.error === 'object') {
    const problem = error.error as { detail?: unknown; title?: unknown };
    for (const text of [problem.detail, problem.title]) {
      if (typeof text === 'string' && text.trim() !== '') {
        return text;
      }
    }
  }
  return fallback;
}

/**
 * Where a store's load stands: `requestStatus` is `idle`, `loading`, `loaded` or `{ error }`. Stores call
 * `setLoading()` before a foreground load, `setLoaded()` after it and `setError(e)` when it fails.
 * (Background refreshes should not call `setLoading()`, so the screen keeps showing what it has.)
 */
export function withRequestStatus() {
  return signalStoreFeature(
    withState<{ requestStatus: RequestStatus }>({ requestStatus: 'idle' }),
    withComputed(({ requestStatus }) => ({
      isLoading: computed(() => requestStatus() === 'loading'),
      isLoaded: computed(() => requestStatus() === 'loaded'),
      error: computed(() => {
        const status = requestStatus();
        return typeof status === 'object' ? status.error : null;
      }),
    })),
    withMethods((store) => ({
      setLoading(): void {
        patchState(store, { requestStatus: 'loading' });
      },
      setLoaded(): void {
        patchState(store, { requestStatus: 'loaded' });
      },
      setError(error: unknown, fallback?: string): void {
        patchState(store, { requestStatus: { error: errorMessage(error, fallback) } });
      },
    })),
  );
}
