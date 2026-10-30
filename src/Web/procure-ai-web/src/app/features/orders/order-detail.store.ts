import { HttpContext } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { ReceiptLineBody } from '../../api/purchasing/models/receipt-line-body';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { problemOf } from '../../core/http/problem-fields';
import { versionEtag, withDetail } from '../../core/state/with-detail';
import { AuthStore } from '../../core/auth/auth.store';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { Logger } from '../../core/logging/logger';
import { canCloseShort, canReceive, invoicedAndPaid } from './order-quantities';

export interface ReceiptInput {
  deliveryNote: string | null;
  lines: ReceiptLineBody[];
}

type Busy = 'idle' | 'receiving' | 'closing';

/** What the page shows: the order and its invoices. */
interface OrderDetail {
  order: PurchaseOrderDto;
  invoices: InvoiceDto[];
}

/** Sent as the reason of a close-short: the screen asks "are you sure", it has no free text field. */
const quietly = () => new HttpContext().set(SKIP_FORBIDDEN_REDIRECT, true);

/**
 * One purchase order: the order (with lines, receipts and supplier state) and its invoices (only with `invoices.read`,
 * one call). Provide it in the page's `providers`. Revalidates in the background when the hub says this order, or an
 * invoice of its request, changed, and after a reconnect. Writes (record delivery, close short) wait for the server,
 * send `If-Match` with the loaded version and one `Idempotency-Key` per (order, version, action, payload), so a retry
 * repeats it and a double click shares the running call; a 412 opens the conflict dialog and loads the latest order.
 */
export const OrderDetailStore = signalStore(
  withDetail<OrderDetail, PurchaseOrderDto>(() => {
    const orders = inject(OrdersService);
    const invoicing = inject(InvoicesService);
    const auth = inject(AuthStore);
    const log = inject(Logger).withSource('OrderDetailStore');
    return {
      // Other services (Invoicing, Purchasing) update their own view of this from the same events: read again after the push.
      entity: ['order', 'invoice'],
      errorMessage: 'The order could not be loaded.',
      isRelevant: (change, id, detail) => change.id === id || (detail !== null && change.requestId === detail.order.requestId),
      fromSeed: (order) => ({ value: { order, invoices: [] }, etag: versionEtag(order) }),
      load: async (id, { background, current, seed }) => {
        const orderCall = seed ? Promise.resolve(seed) : orders.getOrder({ id }, quietly());
        const invoicesCall: Promise<InvoiceDto[]> = auth.hasPermission('invoices.read')
          ? invoicing.listInvoices({ orderId: id, page: 1, pageSize: 50 }, quietly()).then((r) => r.items)
          : Promise.resolve([]);
        const [order, invoices] = await Promise.allSettled([orderCall, invoicesCall]);
        if (order.status === 'rejected') {
          if (!(background && current)) {
            throw order.reason; // nothing to show
          }
          log.warn('Refreshing the order failed; keeping what is shown', order.reason);
        }
        if (invoices.status === 'rejected') {
          log.warn('Loading the invoices failed', invoices.reason);
        }
        const value: OrderDetail = {
          order: order.status === 'fulfilled' ? order.value : current!.order,
          invoices: invoices.status === 'fulfilled' ? invoices.value : (current?.invoices ?? []),
        };
        return { value, etag: versionEtag(value.order), partial: invoices.status === 'rejected' ? ['invoices'] : [] };
      },
      resetState: { fieldErrors: {} },
    };
  }),
  withState<{ fieldErrors: Record<string, string[]> }>({ fieldErrors: {} }),
  withComputed((store) => {
    const auth = inject(AuthStore);
    const canSeeInvoices = computed(() => auth.hasPermission('invoices.read'));
    const mayReceive = computed(() => auth.hasPermission('orders.receive'));
    const order = computed(() => store.value()?.order ?? null);
    return {
      orderId: store.id,
      order,
      invoices: computed(() => store.value()?.invoices ?? []),
      /** The invoices could not be loaded (the order itself did). */
      invoicesFailed: computed(() => store.partial().includes('invoices')),
      busy: computed(() => (store.busyKind() ?? 'idle') as Busy),
      canSeeInvoices,
      canSeeRequest: computed(() => auth.hasPermission('requests.read')),
      /** Record delivery is offered: the right, and goods can be received. */
      showReceiptForm: computed(() => mayReceive() && !!order() && canReceive(order()!)),
      showCloseShort: computed(() => mayReceive() && !!order() && canCloseShort(order()!)),
      /** Invoiced and paid units per order line number. */
      invoicedByLine: computed(() => invoicedAndPaid(store.value()?.invoices ?? [])),
    };
  }),
  withMethods((store) => {
    const orders = inject(OrdersService);

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

    const write = (
      kind: Exclude<Busy, 'idle'>,
      order: PurchaseOrderDto,
      call: (etag: string, key: string) => Promise<PurchaseOrderDto>,
      what: string,
      keyParts: (string | number | null)[],
    ): Promise<boolean> =>
      store.runWrite((etag, key) => call(etag ?? versionEtag(order), key), {
        kind,
        what,
        keyParts: [order.id, order.version, ...keyParts],
        failureMessage: `${what[0].toUpperCase()}${what.slice(1)} did not work. Please try again.`,
        conflictMessage: 'Someone else just changed this order. Showing the latest version.',
        apply: (updated, current) => ({ value: { order: updated, invoices: current?.invoices ?? [] }, etag: versionEtag(updated) }),
        reloadAfter: true, // the invoices (and anything the server added) in the background
        onError: (error) => {
          const problem = problemOf(error);
          if (Object.keys(problem.fields).length === 0) {
            return undefined;
          }
          // Field errors that belong to no order line (`lines`, `deliveryNote` ...) have no input to sit under: show them as the general message.
          const { general: unplaced, ...byLine } = lineErrors(problem.fields);
          patchState(store, { fieldErrors: byLine });
          return unplaced?.join(' ') ?? null;
        },
      });

    return {
      /** Shows order `id`. `initial` is what the route resolver already fetched, so it is not asked for twice. */
      init: (id: string, initial: PurchaseOrderDto | null): Promise<void> => store.open(id, initial),

      /** Records a delivery. Only lines with a quantity above zero are sent. Resolves true when the server agreed. */
      receive(input: ReceiptInput): Promise<boolean> {
        const order = store.order();
        if (!order) {
          return Promise.resolve(false);
        }
        const lines = input.lines.filter((l) => l.quantity > 0);
        const note = input.deliveryNote?.trim() ? input.deliveryNote.trim() : null;
        postedLineNos = lines.map((l) => l.lineNo);
        return write(
          'receiving',
          order,
          (etag, key) => orders.receiveGoods({ id: order.id, 'If-Match': etag, 'Idempotency-Key': key, body: { deliveryNote: note, lines } }, quietly()),
          'recording the delivery',
          ['receive', note, JSON.stringify(lines)],
        );
      },

      /** Closes the order short: what is still outstanding will not be delivered. */
      closeShort(reason: string): Promise<boolean> {
        const order = store.order();
        if (!order) {
          return Promise.resolve(false);
        }
        return write(
          'closing',
          order,
          (etag, key) => orders.closeOrderShort({ id: order.id, 'If-Match': etag, 'Idempotency-Key': key, body: { reason: reason.trim() } }, quietly()),
          'closing the order short',
          ['close-short', reason.trim()],
        );
      },
    };
  }),
);
