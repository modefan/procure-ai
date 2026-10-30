import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { MatrixDto } from '../../../api/access/models/matrix-dto';
import { AccessService } from '../../../api/access/services/access.service';
import { isConflict, problemOf } from '../../../core/http/problem-fields';
import { LiveConnection } from '../../../core/live/live-connection';
import { Logger } from '../../../core/logging/logger';
import { versionEtag } from '../../../core/state/with-detail';
import { withCache } from '../../../core/state/with-cache';
import { withRequestStatus } from '../../../core/state/with-request-status';
import { ConflictDialog } from '../../../core/ui/conflict-dialog';

const SAVE_FAILED = 'The changes could not be saved. Please try again.';

/** The roles in the order of the screen; a role the server adds later is placed after them. */
const ROLE_ORDER = ['requester', 'manager', 'department-head', 'finance', 'admin'];
const ROLE_LABELS: Record<string, string> = {
  requester: 'Requester',
  manager: 'Manager',
  'department-head': 'Department head',
  finance: 'Finance',
  admin: 'Admin',
};

/** The screen name of a role (`department-head` becomes "Department head"). */
export function roleLabel(role: string): string {
  return ROLE_LABELS[role] ?? role.charAt(0).toUpperCase() + role.slice(1).replace(/-/g, ' ');
}

type Draft = Record<string, ReadonlySet<string>>;

const draftOf = (matrix: MatrixDto): Draft => Object.fromEntries(matrix.roles.map((r) => [r.role, new Set(r.permissions)]));

const sameSet = (a: ReadonlySet<string> | undefined, b: ReadonlySet<string>): boolean => !!a && a.size === b.size && [...b].every((x) => a.has(x));

interface MatrixState {
  /** The matrix as the server last answered; `version` is what `If-Match` carries. */
  matrix: MatrixDto | null;
  /** The edits on screen: for each role the permissions it will have after Save. */
  draft: Draft;
  saving: boolean;
  /** The roles saved by the last Save (also those saved before a later role failed). */
  savedRoles: string[];
  /** The server's refusal of the last Save (lockout, unknown permission ...), in words; names the roles saved before it. */
  actionError: string | null;
  /** Another admin saved a newer matrix while this one has unsaved edits: the screen offers "Reload". */
  changedElsewhere: boolean;
}

/**
 * The role-permission matrix (screen 14, `admin.authorization`), edited as a draft and saved role by role (plan 06 ruling 8): one
 * PUT per changed role in the matrix's role order, each with the version of the answer before. If one fails, the roles saved so far
 * stay saved and are named, and the matrix is read again. A 412/409 means somebody else saved first: the conflict dialog opens
 * and the edits are replaced by the latest matrix. A refusal that is not a conflict (422: the admin role must keep
 * `admin.authorization`) keeps the unsaved edits so they can be corrected.
 * A push `access-matrix` re-reads the matrix; with unsaved edits it only offers "Reload" when the server's version really is newer
 * (our own save echoes back as a push too).
 */
export const MatrixStore = signalStore(
  { providedIn: 'root' },
  withState<MatrixState>({ matrix: null, draft: {}, saving: false, savedRoles: [], actionError: null, changedElsewhere: false }),
  withRequestStatus(),
  withCache(),
  withComputed((store) => ({
    /** The roles in screen order. */
    roles: computed(() => {
      const names = store.matrix()?.roles.map((r) => r.role) ?? [];
      const rank = (role: string) => (ROLE_ORDER.indexOf(role) === -1 ? ROLE_ORDER.length : ROLE_ORDER.indexOf(role));
      return [...names].sort((a, b) => rank(a) - rank(b));
    }),
    permissions: computed(() => store.matrix()?.availablePermissions ?? []),
  })),
  withComputed((store) => {
    const changedRoles = computed(() => {
      const matrix = store.matrix();
      const draft = store.draft();
      if (!matrix) {
        return [];
      }
      const saved = new Map(matrix.roles.map((r) => [r.role, new Set(r.permissions)]));
      return store.roles().filter((role) => !sameSet(draft[role], saved.get(role) ?? new Set()));
    });
    return { changedRoles, dirty: computed(() => changedRoles().length > 0) };
  }),
  withMethods((store) => {
    const api = inject(AccessService);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('MatrixStore');
    let latest = 0;
    let shown: number | null = null;
    let pushedWhileSaving = false;

    const show = (matrix: MatrixDto, keep: Draft = {}) =>
      patchState(store, { matrix, draft: { ...draftOf(matrix), ...keep }, changedElsewhere: false });

    /** Reads the matrix. Unsaved edits are kept unless `replace`; then a newer version only raises `changedElsewhere`. */
    const fetchMatrix = async (replace: boolean, keep: Draft = {}): Promise<void> => {
      const call = ++latest;
      const epoch = store.cacheEpoch();
      if (shown !== epoch && store.matrix() === null) {
        store.setLoading();
      }
      try {
        const matrix = await api.getMatrix();
        if (call !== latest || epoch !== store.cacheEpoch()) {
          return;
        }
        const current = store.matrix();
        if (!replace && current && store.dirty()) {
          if (matrix.version !== current.version) {
            patchState(store, { changedElsewhere: true });
          }
        } else {
          show(matrix, keep);
        }
        shown = epoch;
        store.setLoaded();
      } catch (error) {
        if (call === latest && epoch === store.cacheEpoch()) {
          log.error('Loading the authorization matrix failed', error);
          store.setError(error, 'The authorization matrix could not be loaded.');
        }
        throw error;
      }
    };

    const names = (roles: string[]) => roles.map(roleLabel).join(', ');

    return {
      /** Shows the matrix. Does nothing while what is shown is fresh. */
      async load(): Promise<void> {
        try {
          await store.cacheLoad('matrix', () => fetchMatrix(false));
        } catch {
          // Already recorded in `error`.
        }
      },

      /** Throws away the edits and shows the latest matrix (the "Reload" of the banner, and "Try again"). */
      async reload(): Promise<void> {
        try {
          await fetchMatrix(true);
        } catch {
          // Already recorded in `error`.
        }
      },

      /** Flips one box of the draft. */
      toggle(role: string, permission: string): void {
        if (store.saving()) {
          return;
        }
        const next = new Set(store.draft()[role] ?? []);
        if (!next.delete(permission)) {
          next.add(permission);
        }
        patchState(store, { draft: { ...store.draft(), [role]: next }, actionError: null });
      },

      /** Puts every box back to what the server has. (Not `reset`: that is the cache feature's, used at logout.) */
      discardChanges(): void {
        const matrix = store.matrix();
        if (matrix && !store.saving()) {
          patchState(store, { draft: draftOf(matrix), actionError: null, changedElsewhere: false });
        }
      },

      clearActionError(): void {
        patchState(store, { actionError: null });
      },

      /** Saves the changed roles one by one. Resolves true when all were saved. */
      async save(): Promise<boolean> {
        const start = store.matrix();
        if (!start || store.saving() || !store.dirty()) {
          return false;
        }
        const epoch = store.cacheEpoch();
        const roles = store.changedRoles();
        const edits = store.draft();
        let version = start.version;
        const saved: string[] = [];
        pushedWhileSaving = false;
        patchState(store, { saving: true, savedRoles: [], actionError: null });
        try {
          for (const role of roles) {
            const answer = await api.setRolePermissions({
              role,
              'If-Match': versionEtag({ version }),
              body: { permissions: [...(edits[role] ?? [])].sort() },
            });
            if (epoch !== store.cacheEpoch()) {
              return false;
            }
            version = answer.version;
            saved.push(role);
            // Roles saved so far show as clean; the edits of the rest stay.
            patchState(store, { matrix: answer, savedRoles: [...saved] });
          }
          patchState(store, { changedElsewhere: false });
          return true;
        } catch (error) {
          if (epoch !== store.cacheEpoch()) {
            return false;
          }
          const before = saved.length > 0 ? `Saved before that: ${names(saved)}. ` : '';
          if (isConflict(error)) {
            await conflict.show(`Someone else changed the authorization matrix. ${before}Showing the latest version.`);
            await fetchMatrix(true).catch(() => undefined);
          } else {
            log.warn('Saving the authorization matrix failed', error);
            const problem = problemOf(error);
            const reason = problem.general ?? (Object.values(problem.fields).flat().join(' ') || SAVE_FAILED);
            // The roles from the refused one on keep their edits, so the cause can be fixed and Save pressed again.
            const keep = Object.fromEntries(roles.filter((r) => !saved.includes(r)).map((r) => [r, edits[r]]));
            await fetchMatrix(true, keep).catch(() => undefined);
            patchState(store, { actionError: `${before}${reason}` });
          }
          return false;
        } finally {
          if (epoch === store.cacheEpoch()) {
            patchState(store, { saving: false });
            if (pushedWhileSaving) {
              pushedWhileSaving = false;
              store.invalidate('matrix');
            }
          }
        }
      },

      /** An `access-matrix` push: read the matrix again (not while saving: the save's own answers are newer). */
      onPush(): void {
        if (store.saving()) {
          pushedWhileSaving = true;
          return;
        }
        store.invalidate('matrix');
      },
    };
  }),
  withHooks((store) => {
    let off: (() => void) | null = null;
    return {
      onInit() {
        off = inject(LiveConnection).onEntityChanged('access-matrix', () => store.onPush());
      },
      onDestroy() {
        off?.();
      },
    };
  }),
);

export type MatrixStore = InstanceType<typeof MatrixStore>;
