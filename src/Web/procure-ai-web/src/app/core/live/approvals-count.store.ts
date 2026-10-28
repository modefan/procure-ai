import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { Logger } from '../logging/logger';
import { withCache } from '../state/with-cache';
import { LiveConnection } from './live-connection';

/**
 * How many approval steps wait for me (the total of my approvals inbox), for the header link. The count is read
 * from the generated approvals client with a page size of one. It is refreshed in the background on every
 * `EntityChanged` with entity `approval` once someone has asked for it, and on every reconnect.
 * The Approvals screen (Task 5) can reuse this store for the same number.
 */
export const ApprovalsCountStore = signalStore(
  { providedIn: 'root' },
  withState({ count: 0 }),
  withCache(),
  withMethods((store) => {
    const api = inject(ApprovalsService);
    const log = inject(Logger).withSource('ApprovalsCountStore');
    return {
      /** Reads the count (fresh within the stale time, so a second call does nothing). Never throws; keeps the last count on failure. */
      async load(): Promise<void> {
        const epoch = store.cacheEpoch();
        try {
          await store.cacheLoad('count', async () => {
            const inbox = await api.getApprovalInbox({ page: 1, pageSize: 1 });
            if (epoch === store.cacheEpoch()) {
              patchState(store, { count: inbox.total });
            }
          });
        } catch (error) {
          log.warn('Loading the approvals count failed', error);
        }
      },
    };
  }),
  withHooks((store) => {
    let off: (() => void) | null = null;
    return {
      onInit() {
        // invalidate() does nothing until `load()` has registered the key, so a push before anyone asked costs nothing.
        off = inject(LiveConnection).onEntityChanged('approval', () => store.invalidate('count'));
      },
      onDestroy() {
        off?.();
      },
    };
  }),
);
