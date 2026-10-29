import { computed, inject } from '@angular/core';
import { signalStore, withComputed, withMethods } from '@ngrx/signals';
import { InvoiceStatus } from '../../api/invoicing/models/invoice-status';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { withPagedList } from '../../core/state/with-paged-list';

export type InvoiceStatusFilter = 'all' | InvoiceStatus;

interface InvoicesFilter {
  status: InvoiceStatusFilter;
}

/**
 * The invoices list, one page at a time (the server limits requesters to the invoices of their own orders; see
 * `withPagedList`). An `invoice` push or a reconnect refreshes the page on screen in the background, moving to another page or
 * filter loads in the foreground.
 */
export const InvoicesStore = signalStore(
  { providedIn: 'root' },
  withPagedList(() => {
    const api = inject(InvoicesService);
    return {
      entity: 'invoice',
      errorMessage: 'The invoices could not be loaded.',
      initialFilter: { status: 'all' } as InvoicesFilter,
      load: (f: InvoicesFilter, page: number, pageSize: number) => api.listInvoices({ page, pageSize, ...(f.status !== 'all' ? { status: f.status } : {}) }),
    };
  }),
  withComputed(({ filter }) => ({ status: computed(() => filter().status) })),
  withMethods((store) => ({
    /** Changes the status filter and shows page 1 of the result. */
    setStatus: (status: InvoiceStatusFilter) => store.setFilter({ status }),
  })),
);
