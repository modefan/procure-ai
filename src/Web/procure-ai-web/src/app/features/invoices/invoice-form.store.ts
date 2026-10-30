import { HttpContext } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoiceLineBody } from '../../api/invoicing/models/invoice-line-body';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { OrdersService } from '../../api/purchasing/services/orders.service';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';
import { Logger } from '../../core/logging/logger';
import { withRequestStatus } from '../../core/state/with-request-status';
import { idempotencyKey } from '../../core/http/idempotency';
import { problemOf } from '../../core/http/problem-fields';

/** What the form sends: only lines with a quantity, stated total including VAT as printed on the invoice. */
export interface InvoiceDraft {
  orderId: string;
  supplierInvoiceNumber: string;
  /** `yyyy-mm-dd` */
  invoiceDate: string;
  lines: InvoiceLineBody[];
  statedTotalInclVat: number;
  /** The scan this invoice was prefilled from; the server links it to the saved invoice. Left out for a manual entry. */
  scanId?: string;
}

export type FieldErrors = Record<string, string[]>;

interface FormState {
  /** The orders the caller sees (the server scopes them), newest first. */
  orders: PurchaseOrderDto[];
  /** There are more orders than one page holds: the list shows the newest ones only. */
  moreOrders: boolean;
  busy: boolean;
  /** A refusal that belongs to no field: 422 for an order that cannot be invoiced, 404, 409 ... */
  generalError: string | null;
  /** Server field errors by `orderId`, `supplierInvoiceNumber`, `invoiceDate`, `statedTotal`, `line.<order line>.quantity|unitPrice`. */
  fieldErrors: FieldErrors;
}

/** Orders that take no invoices (the server refuses them with a 422), so they are not offered. */
const DEAD = new Set<string>(['Cancelled', 'Rejected', 'ClosedShort']);
const quietly = () => new HttpContext().set(SKIP_FORBIDDEN_REDIRECT, true);

/**
 * Writes of the manual-entry form. Provide it in the page's `providers`, so every visit starts clean. One
 * `Idempotency-Key` per submission: the same data repeats its key (the server replays the first result), changed data
 * gets a new key; a second call while one runs shares it (double click). Writes wait for the server.
 */
export const InvoiceFormStore = signalStore(
  withState<FormState>({ orders: [], moreOrders: false, busy: false, generalError: null, fieldErrors: {} }),
  withRequestStatus(),
  withComputed(({ busy }) => ({ isBusy: computed(() => busy()) })),
  withMethods((store) => {
    const invoices = inject(InvoicesService);
    const orders = inject(OrdersService);
    const log = inject(Logger).withSource('InvoiceFormStore');
    const keys = new Map<string, string>();
    let running: Promise<InvoiceDto | null> | null = null;

    const keyFor = (data: string): string => {
      let key = keys.get(data);
      if (!key) {
        key = idempotencyKey();
        keys.set(data, key);
      }
      return key;
    };

    /** `Lines[1].Quantity` (position in what was sent) to `line.<order line number>.quantity`. */
    const mapFields = (fields: Record<string, string[]>, sent: InvoiceLineBody[]): { byField: FieldErrors; unplaced: string[] } => {
      const byField: FieldErrors = {};
      const unplaced: string[] = [];
      const add = (key: string, messages: string[]) => (byField[key] = [...(byField[key] ?? []), ...messages]);
      for (const [path, messages] of Object.entries(fields)) {
        const line = /^lines(?:\[|\.)(\d+)\]?\.(\w+)$/.exec(path);
        if (line) {
          const lineNo = sent[Number(line[1])]?.lineNo;
          const prop = line[2] === 'unitpriceexclvat' ? 'unitPrice' : line[2];
          if (lineNo !== undefined && (prop === 'quantity' || prop === 'unitPrice')) {
            add(`line.${lineNo}.${prop}`, messages);
          } else {
            unplaced.push(...messages);
          }
          continue;
        }
        switch (path) {
          case 'orderid':
            add('orderId', messages);
            break;
          case 'supplierinvoicenumber':
            add('supplierInvoiceNumber', messages);
            break;
          case 'invoicedate':
            add('invoiceDate', messages);
            break;
          case 'statedtotalinclvat':
            add('statedTotal', messages);
            break;
          default:
            unplaced.push(...messages);
        }
      }
      return { byField, unplaced };
    };

    return {
      /** Loads the orders to choose from (one call; the server limits requesters to their own). */
      async loadOrders(): Promise<void> {
        store.setLoading();
        try {
          const result = await orders.listOrders({ page: 1, pageSize: 100 });
          patchState(store, { orders: result.items.filter((o) => !DEAD.has(o.status)), moreOrders: result.total > result.items.length });
          store.setLoaded();
        } catch (error) {
          log.error('Loading the orders failed', error);
          store.setError(error, 'The orders could not be loaded.');
        }
      },

      /** Records the invoice. Resolves with it, or null when the server refused (see `generalError` and `fieldErrors`). */
      create(draft: InvoiceDraft): Promise<InvoiceDto | null> {
        if (running) {
          return running;
        }
        patchState(store, { busy: true, generalError: null, fieldErrors: {} });
        const call: Promise<InvoiceDto | null> = invoices
          .createInvoice({ 'Idempotency-Key': keyFor(JSON.stringify(draft)), body: draft }, quietly())
          .catch((error: unknown) => {
            log.warn('Recording the invoice failed', error);
            const problem = problemOf(error);
            const hasFields = Object.keys(problem.fields).length > 0;
            if (hasFields) {
              const { byField, unplaced } = mapFields(problem.fields, draft.lines);
              patchState(store, { fieldErrors: byField, generalError: unplaced.length > 0 ? unplaced.join(' ') : null });
            } else {
              patchState(store, { generalError: problem.general ?? 'The invoice could not be saved. Please try again.' });
            }
            return null;
          })
          .finally(() => {
            running = null;
            patchState(store, { busy: false });
          });
        running = call;
        return call;
      },

      clearErrors(): void {
        if (store.generalError() !== null || Object.keys(store.fieldErrors()).length > 0) {
          patchState(store, { generalError: null, fieldErrors: {} });
        }
      },
    };
  }),
);
