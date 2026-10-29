import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { removeEntity } from '@ngrx/signals/entities';
import { ApprovalStepDto } from '../../api/approvals/models/approval-step-dto';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { withPagedList } from '../../core/state/with-paged-list';
import { Logger } from '../../core/logging/logger';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { idempotencyKey } from '../../core/http/idempotency';
import { isConflict, problemOf } from '../../core/http/problem-fields';

export type InboxAction = 'approve' | 'reject';

interface InboxState {
  /** The row being approved or rejected, by step ID: its buttons are busy. */
  acting: Record<string, InboxAction>;
  /** A refusal (403, 422 ...) or validation message for a row, by step ID. */
  rowErrors: Record<string, string>;
  /** The earliest time any waiting step escalates (the first row of page 1; kept while another page is shown), for the dashboard. */
  soonestDueAt: string | null;
}

const quietly = () => new HttpContext().set(SKIP_FORBIDDEN_REDIRECT, true);

/**
 * My approvals inbox, one page at a time: the open steps I may approve or reject, most urgent first. The header
 * count reads `total()` from here, so the number and the list are one source (asking again within the stale time
 * does nothing; an `approval` push or a reconnect refreshes the page on screen in the background).
 *
 * Writes wait for the server, send `If-Match` with the step's version and one `Idempotency-Key` per (step, action,
 * version[, reason]); a double click shares the running call. 412 opens the conflict dialog and reloads the inbox;
 * any other refusal is shown as the server's message on the row. The row leaves the list only after the server agrees.
 */
export const ApprovalsStore = signalStore(
  { providedIn: 'root' },
  withPagedList(() => {
    const api = inject(ApprovalsService);
    return {
      entity: 'approval',
      trailing: true,
      errorMessage: 'The approvals inbox could not be loaded.',
      initialFilter: {},
      load: (_filter: object, page: number, pageSize: number) => api.getApprovalInbox({ page, pageSize }),
      afterLoad: (result, { load, patch }) => {
        if (result.page === 1) {
          patch({ soonestDueAt: result.items.map((s) => s.dueAt).filter((d): d is string => !!d).sort()[0] ?? null });
        }
        if (result.items.length === 0 && result.total > 0 && result.page > 1) {
          // Rows left this page meanwhile (someone else acted): show the last page that still has some.
          void load(Math.max(1, Math.ceil(result.total / result.pageSize)));
        }
      },
    };
  }),
  withState<InboxState>({ acting: {}, rowErrors: {}, soonestDueAt: null }),
  withMethods((store) => {
    const api = inject(ApprovalsService);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('ApprovalsStore');
    const running = new Map<string, Promise<boolean>>();
    const keys = new Map<string, string>();

    const keyFor = (...parts: (string | number)[]): string => {
      const id = parts.join(':');
      let key = keys.get(id);
      if (!key) {
        key = idempotencyKey();
        keys.set(id, key);
      }
      return key;
    };
    const setRowError = (stepId: string, message: string | null) =>
      patchState(store, (s) => {
        const next = { ...s.rowErrors };
        if (message === null) {
          delete next[stepId];
        } else {
          next[stepId] = message;
        }
        return { rowErrors: next };
      });
    const setActing = (stepId: string, action: InboxAction | null) =>
      patchState(store, (s) => {
        const next = { ...s.acting };
        if (action === null) {
          delete next[stepId];
        } else {
          next[stepId] = action;
        }
        return { acting: next };
      });

    const methods = {
      /** The action running on this row, if any. */
      actingOn: (stepId: string): InboxAction | null => store.acting()[stepId] ?? null,
      /** The message on this row, if any. */
      errorOf: (stepId: string): string | null => store.rowErrors()[stepId] ?? null,

      /** Loads the page on screen (the header calls this; it costs nothing while fresh). */
      refresh(): Promise<void> {
        return store.load(store.page());
      },

      /** Approves the step with the version that was read. Resolves true when the server agreed. */
      approve(step: ApprovalStepDto): Promise<boolean> {
        return act(step, 'approve', () =>
          api.approveStep({ stepId: step.id, 'If-Match': `"${step.version}"`, 'Idempotency-Key': keyFor(step.id, 'approve', step.version) }, quietly()),
        );
      },

      /** Rejects the step; a reason is required. Resolves true when the server agreed. */
      reject(step: ApprovalStepDto, reason: string): Promise<boolean> {
        const text = reason.trim();
        if (!text) {
          setRowError(step.id, 'Enter a reason for rejecting.');
          return Promise.resolve(false);
        }
        return act(step, 'reject', () =>
          api.rejectStep(
            { stepId: step.id, 'If-Match': `"${step.version}"`, 'Idempotency-Key': keyFor(step.id, 'reject', step.version, text), body: { reason: text } },
            quietly(),
          ),
        );
      },

      /** Forgets the message on a row (the user started something else). */
      clearRowError(stepId: string): void {
        setRowError(stepId, null);
      },
    };

    function act(step: ApprovalStepDto, action: InboxAction, call: () => Promise<unknown>): Promise<boolean> {
      const existing = running.get(step.id);
      if (existing) {
        return existing; // a double click, or another button on a row that is busy, shares the running call
      }
      const epoch = store.cacheEpoch();
      setRowError(step.id, null);
      setActing(step.id, action);
      const work = (async (): Promise<boolean> => {
        try {
          await call();
          if (epoch !== store.cacheEpoch()) {
            return true;
          }
          patchState(store, removeEntity(step.id), (s) => ({ total: Math.max(0, s.total - 1) }));
          if (store.entities().length === 0 && store.page() > 1) {
            await store.load(store.page() - 1);
          } else {
            store.invalidate('list'); // the server's view, in the background
          }
          return true;
        } catch (error) {
          if (isConflict(error)) {
            // 412: the step changed since it was read. 409: it is not open any more (someone else got there first; the server
            // checks that before the version). Either way the user sees the same conflict and the latest inbox.
            await conflict.show(`Someone else just changed ${step.requestNumber}. Showing the latest version.`);
            store.invalidate('list');
          } else {
            log.warn(`${action} of a step failed`, error);
            setRowError(step.id, problemOf(error).general ?? `The request could not be ${action === 'approve' ? 'approved' : 'rejected'}. Please try again.`);
          }
          return false;
        } finally {
          running.delete(step.id);
          setActing(step.id, null);
        }
      })();
      running.set(step.id, work);
      return work;
    }

    return methods;
  }),
);
