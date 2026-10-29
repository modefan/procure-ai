import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { PayLineBody } from '../../api/invoicing/models/pay-line-body';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { AuthStore } from '../../core/auth/auth.store';
import { AppConfigService } from '../../core/config/app-config';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { EntityChanged } from '../../core/live/live.models';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { CacheRegistry } from '../../core/state/cache-registry';
import { createTrailingReload } from '../../core/state/trailing-reload';
import { withRequestStatus } from '../../core/state/with-request-status';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { idempotencyKey } from '../../core/http/idempotency';
import { isConflict, problemOf } from '../../core/http/problem-fields';

type Busy = 'idle' | 'paying' | 'holding' | 'releasing';

interface DetailState {
  invoiceId: string | null;
  invoice: InvoiceDto | null;
  busy: Busy;
  /** A refusal of the last write: the server's own message (403 separation of duties, 422 nothing payable, ...). */
  generalError: string | null;
}

const EMPTY: DetailState = { invoiceId: null, invoice: null, busy: 'idle', generalError: null };
/** A refusal is shown on the page, so the error interceptor must not redirect a 403 to /forbidden. */
const quietly = () => new HttpContext().set(SKIP_FORBIDDEN_REDIRECT, true);

/**
 * One invoice with its three-way match and payments. Provide it in the page's `providers`. Revalidates in the
 * background when the hub says this invoice, or anything of its request (the order, a delivery), changed, and after a
 * reconnect. Writes (pay, hold, release hold) need `invoices.release-payment`, wait for the server, send `If-Match`
 * with the loaded version and one `Idempotency-Key` per (invoice, version, action, payload), so a retry repeats it and a
 * double click shares the running call. A 412 (or 409) opens the conflict dialog and loads the latest invoice; any other
 * refusal is shown with the server's message.
 */
export const InvoiceDetailStore = signalStore(
  withState<DetailState>(EMPTY),
  withRequestStatus(),
  withComputed((store) => {
    const auth = inject(AuthStore);
    return {
      mayRelease: computed(() => auth.hasPermission('invoices.release-payment')),
      canSeeOrder: computed(() => auth.hasPermission('orders.read')),
      canSeeRequest: computed(() => auth.hasPermission('requests.read')),
      /** Something can be paid right now. */
      canPay: computed(() => (store.invoice()?.payableInclVat ?? 0) > 0 && !store.invoice()?.onHold),
    };
  }),
  withMethods((store) => {
    const api = inject(InvoicesService);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('InvoiceDetailStore');
    // Other services (Invoicing, Purchasing) update their own view of this from the same events: read again after the push.
    const config = inject(AppConfigService);
    const trailing = createTrailingReload(() => void reload(true), () => config.config().cache.trailingReloadMs);

    /** Changes at init and logout, so an answer on its way for the invoice before is dropped. */
    let generation = 0;
    let running: Promise<void> | null = null;
    let again = false;
    let seed: InvoiceDto | null = null;
    let working: Promise<boolean> | null = null;
    let workingKind: Busy = 'idle';
    const keys = new Map<string, string>();

    const keyFor = (...parts: (string | number | null)[]): string => {
      const id = parts.join('|');
      let key = keys.get(id);
      if (!key) {
        key = idempotencyKey();
        keys.set(id, key);
      }
      return key;
    };

    const loadOne = async (background: boolean): Promise<void> => {
      const id = store.invoiceId();
      if (!id) {
        return;
      }
      const mine = generation;
      if (!background) {
        store.setLoading();
      }
      const fromSeed = seed;
      seed = null;
      try {
        const invoice = fromSeed ?? (await api.getInvoice({ id }, quietly()));
        if (mine !== generation || store.invoiceId() !== id) {
          return;
        }
        patchState(store, { invoice });
        store.setLoaded();
      } catch (error) {
        if (mine !== generation || store.invoiceId() !== id) {
          return;
        }
        if (background && store.invoice()) {
          log.warn('Refreshing the invoice failed; keeping what is shown', error);
        } else {
          log.error('Loading the invoice failed', error);
          store.setError(error, 'The invoice could not be loaded.');
        }
      }
    };

    /** Runs one load; a call while one runs asks for exactly one more load afterwards. */
    const reload = (background: boolean): Promise<void> => {
      if (running) {
        again = true;
        return running;
      }
      const call: Promise<void> = loadOne(background)
        .catch((error: unknown) => log.error('Loading the invoice failed unexpectedly', error))
        .finally(() => {
          if (running !== call) {
            return;
          }
          running = null;
          if (again) {
            again = false;
            void reload(true);
          }
        });
      running = call;
      return call;
    };

    const write = (busy: Busy, call: (invoice: InvoiceDto) => Promise<InvoiceDto>, what: string): Promise<boolean> => {
      const invoice = store.invoice();
      if (!invoice) {
        return Promise.resolve(false);
      }
      if (working) {
        // A double click shares the running call; a different action while one runs is refused, so it cannot report the other's result.
        return busy === workingKind ? working : Promise.resolve(false);
      }
      workingKind = busy;
      const mine = generation;
      patchState(store, { busy, generalError: null });
      working = (async () => {
        try {
          const updated = await call(invoice);
          if (mine !== generation) {
            return true;
          }
          patchState(store, { invoice: updated });
          return true;
        } catch (error) {
          if (mine !== generation) {
            return false;
          }
          if (isConflict(error)) {
            await conflict.show('Someone else just changed this invoice. Showing the latest version.');
            await reload(true);
            patchState(store, { generalError: null });
          } else {
            log.warn(`${what} failed`, error);
            const problem = problemOf(error);
            const fields = Object.values(problem.fields).flat().join(' ');
            patchState(store, { generalError: problem.general ?? (fields || `${what[0].toUpperCase()}${what.slice(1)} did not work. Please try again.`) });
            if (busy === 'paying' && error instanceof HttpErrorResponse && error.status === 422) {
              // What can be paid changed under the screen: show the current amount (the message stays).
              void reload(true);
            }
          }
          return false;
        } finally {
          if (mine === generation) {
            working = null;
            patchState(store, { busy: 'idle' });
          }
        }
      })();
      return working;
    };

    return {
      /** Shows invoice `id`. `initial` is what the route resolver already fetched, so it is not asked for twice. */
      async init(id: string, initial: InvoiceDto | null): Promise<void> {
        generation++;
        trailing.cancel();
        running = null;
        again = false;
        working = null;
        seed = initial;
        patchState(store, { ...EMPTY, invoiceId: id, invoice: initial });
        await reload(false);
      },

      /** Loads again, keeping what is shown (retry button). */
      reload(): Promise<void> {
        patchState(store, { generalError: null });
        return reload(true);
      },

      /** Pays everything payable (`lines` null) or the chosen quantities. Resolves true when the server agreed. */
      pay(lines: PayLineBody[] | null): Promise<boolean> {
        return write(
          'paying',
          (invoice) =>
            api.releasePayment(
              { id: invoice.id, 'If-Match': `"${invoice.version}"`, 'Idempotency-Key': keyFor(invoice.id, invoice.version, 'pay', JSON.stringify(lines)), body: { lines } },
              quietly(),
            ),
          'the payment',
        );
      },

      /** Holds the invoice: nothing is paid on it until the hold is released. */
      hold(reason: string): Promise<boolean> {
        const text = reason.trim();
        return write(
          'holding',
          (invoice) =>
            api.holdInvoice({ id: invoice.id, 'If-Match': `"${invoice.version}"`, 'Idempotency-Key': keyFor(invoice.id, invoice.version, 'hold', text), body: { reason: text } }, quietly()),
          'holding the invoice',
        );
      },

      /** Releases the hold; the server matches the invoice again. */
      releaseHold(): Promise<boolean> {
        return write(
          'releasing',
          (invoice) =>
            api.releaseInvoiceHold({ id: invoice.id, 'If-Match': `"${invoice.version}"`, 'Idempotency-Key': keyFor(invoice.id, invoice.version, 'release-hold') }, quietly()),
          'releasing the hold',
        );
      },

      /** Logout: forget everything and drop answers still on their way. */
      clearCache(): void {
        generation++;
        trailing.cancel();
        running = null;
        working = null;
        seed = null;
        patchState(store, EMPTY);
      },

      /** A push: refresh quietly when it is about this invoice or its request. */
      onChange(change: EntityChanged): void {
        const invoice = store.invoice();
        const id = store.invoiceId();
        if (!id) {
          return;
        }
        if (change.id === id || (invoice !== null && change.requestId === invoice.requestId)) {
          void reload(true);
          trailing.schedule();
        }
      },

      /** Stops the pending trailing load (the page is going away). */
      stop(): void {
        trailing.cancel();
      },
    };
  }),
  withHooks((store) => {
    const live = inject(LiveConnection);
    let release: (() => void)[] = [];
    return {
      onInit() {
        release = [
          inject(CacheRegistry).register(store),
          ...['invoice', 'order'].map((entity) => live.onEntityChanged(entity, (change) => store.onChange(change))),
          live.onReconnected(() => void store.reload()),
        ];
      },
      onDestroy() {
        release.forEach((off) => off());
        store.stop();
      },
    };
  }),
);
