import { inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';
import { removeEntity, setAllEntities, setEntity, withEntities } from '@ngrx/signals/entities';
import { PolicyDocumentDto } from '../../api/assistant/models/policy-document-dto';
import { PolicyDocumentsService } from '../../api/assistant/services/policy-documents.service';
import { idempotencyKey } from '../../core/http/idempotency';
import { isConflict, problemOf } from '../../core/http/problem-fields';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { withCache } from '../../core/state/with-cache';
import { withRequestStatus } from '../../core/state/with-request-status';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { ConflictDialog } from '../../core/ui/conflict-dialog';

/** The 10 MB limit the Assistant service enforces (`Assistant:Uploads:MaxBytes`); the drop zone checks it before sending. */
export const POLICY_MAX_BYTES = 10 * 1024 * 1024;
const PAGE_SIZE = 100;
const ACTION_FAILED = 'The change could not be made. Please try again.';

interface PoliciesState {
  /** True while an upload runs. */
  uploading: boolean;
  /** The document being replaced or removed; null when none. */
  busyId: string | null;
  /** The server's refusal of the last upload, replace or remove (413, 415, 422 ...), in words. */
  actionError: string | null;
}

/**
 * The policy documents the assistant answers from (screen 17, `admin.policies`). Stale-while-revalidate like the other lists;
 * a `policy-document` push (ingestion progress, status changes) refreshes the rows in the background. Upload, replace and
 * remove wait for the server (the file is ingested afterwards, in the background: its progress comes by push). Replace and
 * remove send `If-Match` with the loaded `rowVersion`; ingestion progress changes it, so a 412 is normal: the conflict dialog
 * opens and the latest list is loaded. Remove asks first.
 */
export const PoliciesStore = signalStore(
  { providedIn: 'root' },
  withEntities<PolicyDocumentDto>(),
  withState<PoliciesState>({ uploading: false, busyId: null, actionError: null }),
  withRequestStatus(),
  withCache(),
  withMethods((store) => {
    const api = inject(PolicyDocumentsService);
    const confirm = inject(ConfirmDialog);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('PoliciesStore');
    let latest = 0;
    let shown: number | null = null;
    let working = false;

    const fetchList = async (): Promise<void> => {
      const call = ++latest;
      const epoch = store.cacheEpoch();
      if (shown !== epoch) {
        store.setLoading();
      }
      try {
        const page = await api.listPolicyDocuments({ page: 1, pageSize: PAGE_SIZE });
        if (call !== latest || epoch !== store.cacheEpoch()) {
          return;
        }
        patchState(store, setAllEntities(page.items));
        shown = epoch;
        store.setLoaded();
      } catch (error) {
        if (call === latest && epoch === store.cacheEpoch()) {
          log.error('Loading the policy documents failed', error);
          store.setError(error, 'The policy documents could not be loaded.');
        }
        throw error;
      }
    };

    /** Runs one write at a time; a 412/409 opens the conflict dialog and reloads, other refusals become `actionError`. */
    const write = async (busyId: string | null, what: string, call: () => Promise<void>): Promise<boolean> => {
      if (working) {
        return false;
      }
      working = true;
      const epoch = store.cacheEpoch();
      patchState(store, busyId === null ? { uploading: true, actionError: null } : { busyId, actionError: null });
      try {
        await call();
        return epoch === store.cacheEpoch();
      } catch (error) {
        if (epoch !== store.cacheEpoch()) {
          return false;
        }
        if (isConflict(error)) {
          await conflict.show('Someone else (or the ingestion) just changed this document. Showing the latest version.');
          await fetchList().catch(() => undefined);
        } else {
          log.warn(`${what} failed`, error);
          const problem = problemOf(error);
          patchState(store, { actionError: problem.general ?? (Object.values(problem.fields).flat().join(' ') || ACTION_FAILED) });
        }
        return false;
      } finally {
        working = false;
        if (epoch === store.cacheEpoch()) {
          patchState(store, { uploading: false, busyId: null });
        }
      }
    };

    const ifMatch = (id: string): string | null => {
      const document = store.entityMap()[id];
      return document ? `"${document.rowVersion}"` : null;
    };

    return {
      /** Shows the documents. Does nothing while what is shown is fresh. */
      async load(): Promise<void> {
        try {
          await store.cacheLoad('list', fetchList);
        } catch {
          // Already recorded in `error`.
        }
      },

      clearActionError(): void {
        patchState(store, { actionError: null });
      },

      /** Uploads a new document (202: it is then ingested in the background). Resolves true when the server took it. */
      upload(file: File): Promise<boolean> {
        const key = idempotencyKey();
        return write(null, 'Uploading the policy document', async () => {
          const created = await api.uploadPolicyDocument({ 'Idempotency-Key': key, body: { file } });
          patchState(store, setEntity(created));
        });
      },

      /** Replaces the file of a document with a new version. */
      replace(id: string, file: File): Promise<boolean> {
        if (!store.entityMap()[id]) {
          return Promise.resolve(false);
        }
        return write(id, 'Replacing the policy document', async () => {
          // Read when sending: ingestion progress changes the rowVersion, so an earlier read can be stale.
          const match = ifMatch(id);
          if (!match) {
            return;
          }
          const updated = await api.replacePolicyDocumentFile({ id, 'If-Match': match, body: { file } });
          patchState(store, setEntity(updated));
        });
      },

      /** Asks, then removes the document. Resolves true when it is gone. */
      async remove(id: string): Promise<boolean> {
        const document = store.entityMap()[id];
        if (!document || working) {
          return false;
        }
        const confirmed = await confirm.ask({
          title: `Remove "${document.title}"?`,
          body: 'The assistant stops answering from it at once. This cannot be undone.',
          confirm: 'Remove',
          cancel: 'Keep it',
        });
        if (!confirmed) {
          return false;
        }
        return write(id, 'Removing the policy document', async () => {
          // After the dialog: progress pushes refresh the list while it is open, so the version read before it can be stale.
          const match = ifMatch(id);
          if (!match) {
            return;
          }
          await api.removePolicyDocument({ id, 'If-Match': match });
          patchState(store, removeEntity(id));
        });
      },
    };
  }),
  withHooks((store) => {
    let off: (() => void) | null = null;
    return {
      onInit() {
        // Progress pushes come in bursts while a document is embedded: merge them into one trailing reload.
        off = inject(LiveConnection).onEntityChanged('policy-document', () => store.invalidate('list', { trailing: true }));
      },
      onDestroy() {
        off?.();
      },
    };
  }),
);

export type PoliciesStore = InstanceType<typeof PoliciesStore>;
