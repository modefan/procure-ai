import { HttpErrorResponse } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { LineBody } from '../../api/requests/models/line-body';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsService } from '../../api/requests/services/requests.service';
import { Logger } from '../../core/logging/logger';
import { withRequestStatus } from '../../core/state/with-request-status';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { problemOf } from './problem-fields';

/** What the form sends. Supplier and cost center are fixed once the draft exists (the API cannot change them). */
export interface RequestDraft {
  title: string;
  supplierId: string;
  costCenterCode: string;
  lines: LineBody[];
}

export type FieldErrors = Record<string, string[]>;

interface FormState {
  /** The saved request, with the version the next write must send in If-Match. Null for a draft not yet created. */
  request: RequestDto | null;
  busy: 'idle' | 'saving' | 'submitting';
  /** A 422 or other error that belongs to no field: shown in an alert. */
  generalError: string | null;
  /** ProblemDetails field errors by form path: `title`, `supplier`, `costCenter`, `lines`, `lines.<i>.sku`, `lines.<i>.quantity`. */
  fieldErrors: FieldErrors;
}

const idempotencyKey = () => crypto.randomUUID();
const fingerprint = (title: string, lines: readonly LineBody[]) => JSON.stringify([title, lines.map((l) => [l.sku, l.quantity])]);

/**
 * Writes of the request form: load a draft, save it (create, or update with `If-Match`), submit it.
 * Provide it in the page's `providers`, so every visit starts clean (it holds nothing worth caching).
 *
 * - One `Idempotency-Key` per submission: the same operation on the same data repeats its key (the server replays the
 *   first result), changed data gets a new key. A second call while one runs shares the running call (double click).
 * - Writes wait for the server; no optimistic updates. 412 shows the conflict dialog and loads the latest version.
 */
export const RequestFormStore = signalStore(
  withState<FormState>({ request: null, busy: 'idle', generalError: null, fieldErrors: {} }),
  withRequestStatus(),
  withComputed(({ busy }) => ({ isBusy: computed(() => busy() !== 'idle') })),
  withMethods((store) => {
    const api = inject(RequestsService);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('RequestFormStore');
    /** What the server holds for title and lines. */
    let saved: string | null = null;
    const keys = new Map<string, string>();
    let running: { operation: 'saving' | 'submitting'; call: Promise<RequestDto | null> } | null = null;

    /** The key for an operation on this exact data: repeated until the data changes. */
    const keyFor = (operation: string, data: string): string => {
      const id = `${operation}:${data}`;
      let key = keys.get(id);
      if (!key) {
        key = idempotencyKey();
        keys.set(id, key);
      }
      return key;
    };

    /** `sent` is what the server was given (it may return the lines in another form); a loaded request is its own record. */
    const setRequest = (request: RequestDto, sent?: { title: string; lines: LineBody[] }) => {
      saved = fingerprint(sent?.title ?? request.title, sent?.lines ?? request.lines);
      patchState(store, { request });
    };

    const fail = async (error: unknown): Promise<null> => {
      const problem = problemOf(error);
      if (error instanceof HttpErrorResponse && error.status === 412) {
        await conflict.show(problem.general ?? 'This request was changed by someone else. The latest version is loaded; check your edits and save again.');
        const current = store.request();
        if (current) {
          try {
            const latest = await api.getRequest({ id: current.id });
            setRequest(latest);
          } catch (reloadError) {
            log.warn('Reloading after a version conflict failed', reloadError);
          }
        }
        patchState(store, { generalError: null, fieldErrors: {} });
        return null;
      }
      log.warn('Saving the request failed', error);
      const hasFields = Object.keys(problem.fields).length > 0;
      patchState(store, { generalError: hasFields ? null : (problem.general ?? 'The request could not be saved. Please try again.'), fieldErrors: problem.fields });
      return null;
    };

    /** Creates or updates; resolves with the saved request. Throws on failure (callers turn it into state). */
    const persist = async (draft: RequestDraft): Promise<RequestDto> => {
      const current = store.request();
      if (current === null) {
        const data = JSON.stringify(draft);
        const created = await api.createRequest({ 'Idempotency-Key': keyFor('create', data), body: draft });
        setRequest(created, draft);
        return created;
      }
      if (saved === fingerprint(draft.title, draft.lines)) {
        return current;
      }
      const updated = await api.updateRequest({ id: current.id, 'If-Match': `"${current.version}"`, body: { title: draft.title, lines: draft.lines } });
      setRequest(updated, draft);
      return updated;
    };

    const exclusive = (busy: 'saving' | 'submitting', work: () => Promise<RequestDto>): Promise<RequestDto | null> => {
      if (running) {
        // The same operation again (a double click) shares the running call; another operation is refused (null), never run in parallel.
        return running.operation === busy ? running.call : Promise.resolve(null);
      }
      patchState(store, { busy, generalError: null, fieldErrors: {} });
      const call: Promise<RequestDto | null> = work()
        .catch((error: unknown) => fail(error))
        .finally(() => {
          running = null;
          patchState(store, { busy: 'idle' });
        });
      running = { operation: busy, call };
      return call;
    };

    return {
      /** Loads a draft for editing. A failure sets `error`. */
      async load(id: string): Promise<void> {
        store.setLoading();
        try {
          setRequest(await api.getRequest({ id }));
          store.setLoaded();
        } catch (error) {
          log.error('Loading the request failed', error);
          store.setError(error, 'The request could not be loaded.');
        }
      },

      /** Creates or updates the draft. Resolves with the saved request, or null when it failed (see the error state). */
      saveDraft: (draft: RequestDraft) => exclusive('saving', () => persist(draft)),

      /** Saves pending edits, then submits. Resolves with the submitted request, or null when something failed. */
      submit: (draft: RequestDraft) =>
        exclusive('submitting', async () => {
          const current = await persist(draft);
          const submitted = await api.submitRequest({
            id: current.id,
            'If-Match': `"${current.version}"`,
            'Idempotency-Key': keyFor('submit', `${current.id}:${current.version}`),
          });
          patchState(store, { request: submitted });
          return submitted;
        }),

      /** True when the server holds exactly this title and these lines (saved or loaded), so nothing the user typed is lost. */
      savedMatches(draft: RequestDraft): boolean {
        return store.request() !== null && saved === fingerprint(draft.title, draft.lines);
      },

      clearErrors(): void {
        if (store.generalError() !== null || Object.keys(store.fieldErrors()).length > 0) {
          patchState(store, { generalError: null, fieldErrors: {} });
        }
      },
    };
  }),
);
