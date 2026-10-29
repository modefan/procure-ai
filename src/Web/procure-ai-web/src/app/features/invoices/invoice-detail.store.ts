import { HttpContext, HttpErrorResponse } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { signalStore, withComputed, withMethods } from '@ngrx/signals';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { PayLineBody } from '../../api/invoicing/models/pay-line-body';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { withDetail, versionEtag } from '../../core/state/with-detail';
import { problemOf } from '../../core/http/problem-fields';
import { AuthStore } from '../../core/auth/auth.store';
import { SKIP_FORBIDDEN_REDIRECT } from '../../core/http/errors.interceptor';

type Busy = 'idle' | 'paying' | 'holding' | 'releasing';

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
  withDetail<InvoiceDto>(() => {
    const api = inject(InvoicesService);
    return {
      entity: ['invoice', 'order'],
      errorMessage: 'The invoice could not be loaded.',
      // About this invoice, or anything of its request (the order, a delivery).
      isRelevant: (change, id, invoice) => change.id === id || (invoice !== null && change.requestId === invoice.requestId),
      fromSeed: (invoice) => ({ value: invoice, etag: versionEtag(invoice) }),
      load: async (id, { seed }) => {
        const invoice = seed ?? (await api.getInvoice({ id }, quietly()));
        return { value: invoice, etag: versionEtag(invoice) };
      },
    };
  }),
  withComputed((store) => {
    const auth = inject(AuthStore);
    return {
      invoiceId: store.id,
      invoice: store.value,
      busy: computed(() => (store.busyKind() ?? 'idle') as Busy),
      mayRelease: computed(() => auth.hasPermission('invoices.release-payment')),
      canSeeOrder: computed(() => auth.hasPermission('orders.read')),
      canSeeRequest: computed(() => auth.hasPermission('requests.read')),
      /** Something can be paid right now. */
      canPay: computed(() => (store.value()?.payableInclVat ?? 0) > 0 && !store.value()?.onHold),
    };
  }),
  withMethods((store) => {
    const api = inject(InvoicesService);

    const write = (kind: Exclude<Busy, 'idle'>, call: (invoice: InvoiceDto, etag: string, key: string) => Promise<InvoiceDto>, what: string, keyParts: (string | number | null)[]): Promise<boolean> => {
      const invoice = store.value();
      if (!invoice) {
        return Promise.resolve(false);
      }
      return store.runWrite((etag, key) => call(invoice, etag ?? versionEtag(invoice), key), {
        kind,
        what,
        keyParts: [invoice.id, invoice.version, ...keyParts],
        failureMessage: `${what[0].toUpperCase()}${what.slice(1)} did not work. Please try again.`,
        conflictMessage: 'Someone else just changed this invoice. Showing the latest version.',
        apply: (updated) => ({ value: updated, etag: versionEtag(updated) }),
        onError: (error) => {
          if (kind === 'paying' && error instanceof HttpErrorResponse && error.status === 422) {
            void store.refresh(); // what can be paid changed under the screen: show the current amount (the message stays)
          }
          const problem = problemOf(error);
          const fields = Object.values(problem.fields).flat().join(' ');
          return problem.general ?? (fields || undefined);
        },
      });
    };

    return {
      /** Shows invoice `id`. `initial` is what the route resolver already fetched, so it is not asked for twice. */
      init: (id: string, initial: InvoiceDto | null): Promise<void> => store.open(id, initial),

      /** Pays everything payable (`lines` null) or the chosen quantities. Resolves true when the server agreed. */
      pay: (lines: PayLineBody[] | null): Promise<boolean> =>
        write('paying', (invoice, etag, key) => api.releasePayment({ id: invoice.id, 'If-Match': etag, 'Idempotency-Key': key, body: { lines } }, quietly()), 'the payment', ['pay', JSON.stringify(lines)]),

      /** Holds the invoice: nothing is paid on it until the hold is released. */
      hold: (reason: string): Promise<boolean> => {
        const text = reason.trim();
        return write('holding', (invoice, etag, key) => api.holdInvoice({ id: invoice.id, 'If-Match': etag, 'Idempotency-Key': key, body: { reason: text } }, quietly()), 'holding the invoice', ['hold', text]);
      },

      /** Releases the hold; the server matches the invoice again. */
      releaseHold: (): Promise<boolean> =>
        write('releasing', (invoice, etag, key) => api.releaseInvoiceHold({ id: invoice.id, 'If-Match': etag, 'Idempotency-Key': key }, quietly()), 'releasing the hold', ['release-hold']),
    };
  }),
);
