import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { UserDto } from '../../../api/access/models/user-dto';
import { AccessService } from '../../../api/access/services/access.service';
import { ApprovalsService } from '../../../api/approvals/services/approvals.service';
import { AuthorizationCheckDto } from '../../../api/approvals/models/authorization-check-dto';
import { RequestsService } from '../../../api/requests/services/requests.service';
import { Logger } from '../../../core/logging/logger';
import { withCache } from '../../../core/state/with-cache';
import { withRequestStatus } from '../../../core/state/with-request-status';

export type CheckAction = 'view' | 'approve';

/** How the last check ended: an answer, "not found" (unknown request, user or another organisation's), or a failure. */
export type CheckOutcome = { kind: 'result'; check: AuthorizationCheckDto } | { kind: 'not-found'; message: string } | { kind: 'error'; message: string };

interface AccessCheckState {
  users: UserDto[];
  checking: boolean;
  outcome: CheckOutcome | null;
}

/** `1039`, `pr-1039` and `PR-1039` all mean `PR-1039`. Returns null for an empty input. */
export function normaliseRequestNumber(raw: string): string | null {
  const text = raw.trim().toUpperCase();
  if (text === '') {
    return null;
  }
  return /^\d+$/.test(text) ? `PR-${text}` : text;
}

const FAILED = 'The check could not be run. Please try again.';
const isStatus = (error: unknown, status: number): boolean => error instanceof HttpErrorResponse && error.status === status;

/**
 * The effective permissions screen (16, `admin.authorization`): asks the Approvals service whether a user may do an action on a
 * request and why, rule by rule. The request number is looked up first (Requests), then the check runs with its ID.
 */
export const AccessCheckStore = signalStore(
  { providedIn: 'root' },
  withState<AccessCheckState>({ users: [], checking: false, outcome: null }),
  withRequestStatus(),
  withCache(),
  withMethods((store) => {
    const access = inject(AccessService);
    const approvals = inject(ApprovalsService);
    const requests = inject(RequestsService);
    const log = inject(Logger).withSource('AccessCheckStore');
    let latest = 0;

    const fetchUsers = async (): Promise<void> => {
      const epoch = store.cacheEpoch();
      if (store.users().length === 0) {
        store.setLoading();
      }
      try {
        const users = await access.listUsers();
        if (epoch !== store.cacheEpoch()) {
          return;
        }
        patchState(store, { users });
        store.setLoaded();
      } catch (error) {
        if (epoch === store.cacheEpoch()) {
          log.error('Loading users failed', error);
          store.setError(error, 'The users could not be loaded.');
        }
        throw error;
      }
    };

    return {
      async load(): Promise<void> {
        try {
          await store.cacheLoad('users', fetchUsers);
        } catch {
          // Already recorded in `error`.
        }
      },

      async reload(): Promise<void> {
        try {
          await fetchUsers();
        } catch {
          // Already recorded in `error`.
        }
      },

      clearOutcome(): void {
        latest++;
        patchState(store, { outcome: null, checking: false });
      },

      /** Runs one check. A newer check (or a cleared result) makes an older answer be dropped. */
      async check(userId: string, action: CheckAction, requestNumber: string): Promise<void> {
        const call = ++latest;
        const epoch = store.cacheEpoch();
        patchState(store, { checking: true, outcome: null });
        const finish = (outcome: CheckOutcome): void => {
          if (call === latest && epoch === store.cacheEpoch()) {
            patchState(store, { checking: false, outcome });
          }
        };
        let requestId: string;
        try {
          requestId = (await requests.getRequestByNumber({ number: requestNumber })).id;
        } catch (error) {
          if (isStatus(error, 404)) {
            finish({ kind: 'not-found', message: `Request ${requestNumber} not found.` });
          } else {
            log.warn('Looking up the request failed', error);
            finish({ kind: 'error', message: FAILED });
          }
          return;
        }
        try {
          finish({ kind: 'result', check: await approvals.checkAuthorization({ userId, requestId, action }) });
        } catch (error) {
          if (isStatus(error, 404)) {
            finish({ kind: 'not-found', message: 'That user or request was not found in your organisation.' });
          } else {
            log.warn('The access check failed', error);
            finish({ kind: 'error', message: FAILED });
          }
        }
      },
    };
  }),
);

export type AccessCheckStore = InstanceType<typeof AccessCheckStore>;
