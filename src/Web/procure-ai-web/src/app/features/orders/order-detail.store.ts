import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { ReceiptLineBody } from '../../api/purchasing/models/receipt-line-body';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { AuthStore } from '../../core/auth/auth.store';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { EntityChanged } from '../../core/live/live.models';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { CacheRegistry } from '../../core/state/cache-registry';
import { withRequestStatus } from '../../core/state/with-request-status';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { problemOf } from '../requests/problem-fields';
import { canCloseShort, canReceive, invoicedAndPaid } from './order-quantities';

export interface ReceiptInput {
  deliveryNote: string | null;
  lines: ReceiptLineBody[];
}

type Busy = 'idle' | 'receiving' | 'closing';

interface DetailState {
  orderId: string | null;
  order: PurchaseOrderDto | null;
  invoices: InvoiceDto[];
  /** The invoices could not be loaded (the order itself did). */
  invoicesFailed: boolean;
  busy: Busy;
  /** A refusal that belongs to no field (422, 403 ...). */
  generalError: string | null;
  /** Server field errors of the last receipt by `line.<order line number>`. */
  fieldErrors: Record<string, string[]>;
}

const EMPTY: DetailState = { orderId: null, order: null, invoices: [], invoicesFailed: false, busy: 'idle', generalError: null, fieldErrors: {} };
/** Sent as the reason of a close-short: the screen asks "are you sure", it has no free text field. */
export const CLOSE_SHORT_REASON = 'Closed short from the orders screen: the remaining quantities will not be delivered.';
const quietly = () => new HttpContext().set(SKIP_FORBIDDEN_REDIRECT, true);

/**
 * One purchase order: the order (with lines, receipts and supplier state) and its invoices (only with `invoices.read`,
 * one call). Provide it in the page's `providers`. Revalidates in the background when the hub says this order, or an
 * invoice of its request, changed, and after a reconnect. Writes (record delivery, close short) wait for the server,
 * send `If-Match` with the loaded version and one `Idempotency-Key` per (order, version, action, payload), so a retry
 * repeats it and a double click shares the running call; a 412 opens the conflict dialog and loads the latest order.
 */
export const OrderDetailStore = signalStore(
  withState<DetailState>(EMPTY),
  withRequestStatus(),
  withComputed((store) => {
    const auth = inject(AuthStore);
    const canSeeInvoices = computed(() => auth.hasPermission('invoices.read'));
    const mayReceive = computed(() => auth.hasPermission('orders.receive'));
    return {
      canSeeInvoices,
      canSeeRequest: computed(() => auth.hasPermission('requests.read')),
      /** Record delivery is offered: the right, and goods can be received. */
      showReceiptForm: computed(() => mayReceive() && !!store.order() && canReceive(store.order()!)),
      showCloseShort: computed(() => mayReceive() && !!store.order() && canCloseShort(store.order()!)),
      /** Invoiced and paid units per order line number. */
      invoicedByLine: computed(() => invoicedAndPaid(store.invoices())),
    };
  }),
  withMethods((store) => {
    const orders = inject(OrdersService);
    const invoicing = inject(InvoicesService);
    const conflict = inject(ConflictDialog);
    const log = inject(Logger).withSource('OrderDetailStore');

    /** Changes at init and logout, so an answer on its way for the order before is dropped. */
    let generation = 0;
    let running: Promise<void> | null = null;
    let again = false;
    let seed: PurchaseOrderDto | null = null;
    let working: Promise<boolean> | null = null;
    const keys = new Map<string, string>();

    const keyFor = (...parts: (string | number | null)[]): string => {
      const id = parts.join('|');
      let key = keys.get(id);
      if (!key) {
        key = crypto.randomUUID();
        keys.set(id, key);
      }
      return key;
    };

    const loadAll = async (background: boolean): Promise<void> => {
      const id = store.orderId();
      if (!id) {
        return;
      }
      const mine = generation;
      if (!background) {
        store.setLoading();
      }
      const fromSeed = seed;
      seed = null;
      const orderCall = fromSeed ? Promise.resolve(fromSeed) : orders.getOrder({ id }, quietly());
      const invoicesCall: Promise<InvoiceDto[]> = store.canSeeInvoices()
        ? invoicing.listInvoices({ orderId: id, page: 1, pageSize: 50 }, quietly()).then((r) => r.items)
        : Promise.resolve([]);
      const [order, invoices] = await Promise.allSettled([orderCall, invoicesCall]);
      if (mine !== generation || store.orderId() !== id) {
        return;
      }
      if (order.status === 'rejected') {
        if (background && store.order()) {
          log.warn('Refreshing the order failed; keeping what is shown', order.reason);
        } else {
          log.error('Loading the order failed', order.reason);
          store.setError(order.reason, 'The order could not be loaded.');
          return;
        }
      }
      if (invoices.status === 'rejected') {
        log.warn('Loading the invoices failed', invoices.reason);
      }
      patchState(store, {
        ...(order.status === 'fulfilled' ? { order: order.value } : {}),
        ...(invoices.status === 'fulfilled' ? { invoices: invoices.value } : {}),
        invoicesFailed: invoices.status === 'rejected',
      });
      store.setLoaded();
    };

    /** Runs one load; a call while one runs asks for exactly one more load afterwards. */
    const reload = (background: boolean): Promise<void> => {
      if (running) {
        again = true;
        return running;
      }
      const call: Promise<void> = loadAll(background)
        .catch((error: unknown) => log.error('Loading the order failed unexpectedly', error))
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

    const write = (busy: Busy, call: () => Promise<PurchaseOrderDto>, what: string): Promise<boolean> => {
      if (working) {
        return working; // a double click shares the running call
      }
      const mine = generation;
      patchState(store, { busy, generalError: null, fieldErrors: {} });
      working = (async () => {
        try {
          const updated = await call();
          if (mine !== generation) {
            return true;
          }
          patchState(store, { order: updated });
          void reload(true); // the invoices (and anything the server added) in the background
          return true;
        } catch (error) {
          if (mine !== generation) {
            return false;
          }
          if (error instanceof HttpErrorResponse && error.status === 412) {
            await conflict.show('Someone else just changed this order. Showing the latest version.');
            await reload(true);
          } else {
            log.warn(`${what} failed`, error);
            const problem = problemOf(error);
            const hasFields = Object.keys(problem.fields).length > 0;
            patchState(store, {
              generalError: hasFields ? null : (problem.general ?? `${what[0].toUpperCase()}${what.slice(1)} did not work. Please try again.`),
              fieldErrors: hasFields ? lineErrors(problem.fields) : {},
            });
          }
          return false;
        } finally {
          working = null;
          if (mine === generation) {
            patchState(store, { busy: 'idle' });
          }
        }
      })();
      return working;
    };

    /** The posted lines are numbered by position; the form wants them by order line number. */
    let postedLineNos: number[] = [];
    const lineErrors = (fields: Record<string, string[]>): Record<string, string[]> => {
      const result: Record<string, string[]> = {};
      for (const [path, messages] of Object.entries(fields)) {
        const match = /^lines\.(\d+)/.exec(path);
        const lineNo = match ? postedLineNos[Number(match[1])] : undefined;
        const key = lineNo === undefined ? 'general' : `line.${lineNo}`;
        result[key] = [...(result[key] ?? []), ...messages];
      }
      return result;
    };

    return {
      /** Shows order `id`. `initial` is what the route resolver already fetched, so it is not asked for twice. */
      async init(id: string, initial: PurchaseOrderDto | null): Promise<void> {
        generation++;
        running = null;
        again = false;
        working = null;
        seed = initial;
        patchState(store, { ...EMPTY, orderId: id, order: initial });
        await reload(false);
      },

      /** Loads again, keeping what is shown (retry button). */
      reload: () => reload(true),

      /** Records a delivery. Only lines with a quantity above zero are sent. Resolves true when the server agreed. */
      receive(input: ReceiptInput): Promise<boolean> {
        const order = store.order();
        if (!order) {
          return Promise.resolve(false);
        }
        const lines = input.lines.filter((l) => l.quantity > 0);
        const note = input.deliveryNote?.trim() ? input.deliveryNote.trim() : null;
        postedLineNos = lines.map((l) => l.lineNo);
        const key = keyFor(order.id, order.version, 'receive', note, JSON.stringify(lines));
        return write(
          'receiving',
          () => orders.receiveGoods({ id: order.id, 'If-Match': `"${order.version}"`, 'Idempotency-Key': key, body: { deliveryNote: note, lines } }, quietly()),
          'recording the delivery',
        );
      },

      /** Closes the order short: what is still outstanding will not be delivered. */
      closeShort(): Promise<boolean> {
        const order = store.order();
        if (!order) {
          return Promise.resolve(false);
        }
        const key = keyFor(order.id, order.version, 'close-short');
        return write(
          'closing',
          () => orders.closeOrderShort({ id: order.id, 'If-Match': `"${order.version}"`, 'Idempotency-Key': key, body: { reason: CLOSE_SHORT_REASON } }, quietly()),
          'closing the order short',
        );
      },

      /** Logout: forget everything and drop answers still on their way. */
      clearCache(): void {
        generation++;
        running = null;
        working = null;
        seed = null;
        patchState(store, EMPTY);
      },

      /** A push: refresh quietly when it is about this order or its request. */
      onChange(change: EntityChanged): void {
        const order = store.order();
        const id = store.orderId();
        if (!id) {
          return;
        }
        if (change.id === id || (order !== null && change.requestId === order.requestId)) {
          void reload(true);
        }
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
          ...['order', 'invoice'].map((entity) => live.onEntityChanged(entity, (change) => store.onChange(change))),
          live.onReconnected(() => void store.reload()),
        ];
      },
      onDestroy() {
        release.forEach((off) => off());
      },
    };
  }),
);
