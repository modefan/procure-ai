import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { CostCenterAdminDto } from '../../../api/access/models/cost-center-admin-dto';
import { UserDto } from '../../../api/access/models/user-dto';
import { AccessService } from '../../../api/access/services/access.service';
import { isConflict, problemOf } from '../../../core/http/problem-fields';
import { Logger } from '../../../core/logging/logger';
import { withCache } from '../../../core/state/with-cache';
import { withRequestStatus } from '../../../core/state/with-request-status';
import { versionEtag } from '../../../core/state/with-detail';
import { ConflictDialog } from '../../../core/ui/conflict-dialog';

const SAVE_FAILED = 'The changes could not be saved. Please try again.';

/** How a Save ended: both parts saved (or nothing to save), lost to someone else (the list is re-read), or refused (errors are set). */
export type SaveOutcome = 'saved' | 'conflict' | 'error';

interface UsersState {
  users: UserDto[];
  costCenters: CostCenterAdminDto[];
  saving: boolean;
  /** The server's refusal of the cost center list (422: unknown code), in words. */
  codesError: string | null;
  /** The server's refusal of the approval limit, in words. */
  limitError: string | null;
}

const sameCodes = (a: string[], b: string[]): boolean => a.length === b.length && [...a].sort().join('|') === [...b].sort().join('|');

/**
 * Users, their cost centers and approval limits (screen 15, `admin.users`). Saving an edit sends the cost centers, then the limit,
 * each only if it changed and each with the version of the answer before (`If-Match`). There is no live push for users, so every
 * save is followed by what the server answered, and a conflict (412/409) opens the conflict dialog and re-reads everything.
 */
export const UsersStore = signalStore(
  { providedIn: 'root' },
  withState<UsersState>({ users: [], costCenters: [], saving: false, codesError: null, limitError: null }),
  withRequestStatus(),
  withCache(),
  withMethods((store) => {
    const api = inject(AccessService);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('UsersStore');
    let latest = 0;

    const fetchAll = async (): Promise<void> => {
      const call = ++latest;
      const epoch = store.cacheEpoch();
      if (store.users().length === 0) {
        store.setLoading();
      }
      try {
        const [users, costCenters] = await Promise.all([api.listUsers(), api.listCostCenters()]);
        if (call !== latest || epoch !== store.cacheEpoch()) {
          return;
        }
        patchState(store, { users, costCenters });
        store.setLoaded();
      } catch (error) {
        if (call === latest && epoch === store.cacheEpoch()) {
          log.error('Loading users failed', error);
          store.setError(error, 'The users could not be loaded.');
        }
        throw error;
      }
    };

    const replace = (user: UserDto) => patchState(store, { users: store.users().map((u) => (u.userId === user.userId ? user : u)) });
    const reasonOf = (error: unknown): string => {
      const problem = problemOf(error);
      return problem.general ?? (Object.values(problem.fields).flat().join(' ') || SAVE_FAILED);
    };

    return {
      async load(): Promise<void> {
        try {
          await store.cacheLoad('users', fetchAll);
        } catch {
          // Already recorded in `error`.
        }
      },

      async reload(): Promise<void> {
        try {
          await fetchAll();
        } catch {
          // Already recorded in `error`.
        }
      },

      clearErrors(): void {
        patchState(store, { codesError: null, limitError: null });
      },

      /** Saves the changed parts of one user's edit: cost centers first, then the limit. */
      async save(user: UserDto, codes: string[], limit: number): Promise<SaveOutcome> {
        if (store.saving()) {
          return 'error';
        }
        const epoch = store.cacheEpoch();
        let current = user;
        patchState(store, { saving: true, codesError: null, limitError: null });
        let step: 'codes' | 'limit' = 'codes';
        try {
          if (!sameCodes(codes, user.costCenterCodes)) {
            current = await api.setUserCostCenters({ userId: user.userId, 'If-Match': versionEtag(current), body: { codes } });
            replace(current);
          }
          step = 'limit';
          if (limit !== user.approvalLimit) {
            current = await api.setApprovalLimit({ userId: user.userId, 'If-Match': versionEtag(current), body: { amount: limit } });
            replace(current);
          }
          return 'saved';
        } catch (error) {
          if (epoch !== store.cacheEpoch()) {
            return 'error';
          }
          if (isConflict(error)) {
            await conflict.show(`Someone else changed ${user.displayName}. Showing the latest version with your changes on top.`);
            await fetchAll().catch(() => undefined);
            return 'conflict';
          }
          log.warn('Saving a user failed', error);
          patchState(store, step === 'codes' ? { codesError: reasonOf(error) } : { limitError: reasonOf(error) });
          return 'error';
        } finally {
          if (epoch === store.cacheEpoch()) {
            patchState(store, { saving: false });
          }
        }
      },
    };
  }),
);

export type UsersStore = InstanceType<typeof UsersStore>;
