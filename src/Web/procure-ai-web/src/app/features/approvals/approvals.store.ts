import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { removeEntity, setAllEntities, withEntities } from '@ngrx/signals/entities';
import { ApprovalStepDto } from '../../api/approvals/models/approval-step-dto';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { AppConfigService } from '../../core/config/app-config';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { withCache } from '../../core/state/with-cache';
import { withRequestStatus } from '../../core/state/with-request-status';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { idempotencyKey } from '../../core/http/idempotency';
import { isConflict, problemOf } from '../../core/http/problem-fields';

export type InboxAction = 'approve' | 'reject';

interface InboxState {
  page: number;
  pageSize: number;
  total: number;
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
  withEntities<ApprovalStepDto>(),
  withState<InboxState>(() => ({ page: 1, pageSize: inject(AppConfigService).config().paging.pageSize, total: 0, acting: {}, rowErrors: {}, soonestDueAt: null })),
  withRequestStatus(),
  withComputed(({ total, pageSize }) => ({
    pageCount: computed(() => Math.max(1, Math.ceil(total() / pageSize()))),
  })),
  withCache(),
  withMethods((store) => {
    const api = inject(ApprovalsService);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('ApprovalsStore');
    let latest = 0;
    let shown: { key: string; epoch: number } | null = null;
    let currentKey: string | null = null;
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

      /** Shows one page (1-based). Only the newest call may change the state; a failure sets `error` and keeps the rows. */
      async load(page: number): Promise<void> {
        const pageSize = store.pageSize();
        const key = `list:${page}:${pageSize}`;
        const onScreen = () => shown !== null && shown.key === key && shown.epoch === store.cacheEpoch();
        if (currentKey !== key) {
          store.clearCache();
          currentKey = key;
        }
        try {
          await store.cacheLoad(key, async () => {
            const call = ++latest;
            const epoch = store.cacheEpoch();
            if (!onScreen()) {
              store.setLoading();
            }
            try {
              const result = await api.getApprovalInbox({ page, pageSize });
              if (call !== latest || epoch !== store.cacheEpoch()) {
                return;
              }
              patchState(store, setAllEntities(result.items), { page: result.page, pageSize: result.pageSize, total: result.total });
              shown = { key, epoch };
              store.setLoaded();
              if (result.page === 1) {
                patchState(store, { soonestDueAt: result.items.map((s) => s.dueAt).filter((d): d is string => !!d).sort()[0] ?? null });
              }
              if (result.items.length === 0 && result.total > 0 && result.page > 1) {
                // Rows left this page meanwhile (someone else acted): show the last page that still has some.
                void methods.load(Math.max(1, Math.ceil(result.total / result.pageSize)));
              }
            } catch (error) {
              if (call === latest && epoch === store.cacheEpoch()) {
                log.error('Loading the approvals inbox failed', error);
                store.setError(error, 'The approvals inbox could not be loaded.');
              }
              throw error;
            }
          });
        } catch {
          // Already recorded in `error`.
        }
      },

      /** Loads the page on screen (the header calls this; it costs nothing while fresh). */
      refresh(): Promise<void> {
        return methods.load(store.page());
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
            await methods.load(store.page() - 1);
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
  withHooks((store) => {
    const live = inject(LiveConnection);
    let off: (() => void) | null = null;
    return {
      onInit() {
        // invalidate() does nothing until a page has been loaded, so a push before anyone asked costs nothing.
        off = live.onEntityChanged('approval', () => store.invalidate('list', { trailing: true }));
      },
      onDestroy() {
        off?.();
      },
    };
  }),
);
