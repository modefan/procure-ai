import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { setAllEntities, withEntities } from '@ngrx/signals/entities';
import { InvoiceStatus } from '../../api/invoicing/models/invoice-status';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { AppConfigService } from '../../core/config/app-config';
import { LiveConnection } from '../../core/live/live-connection';
import { Logger } from '../../core/logging/logger';
import { withCache } from '../../core/state/with-cache';
import { withRequestStatus } from '../../core/state/with-request-status';

export type InvoiceStatusFilter = 'all' | InvoiceStatus;

interface InvoicesState {
  status: InvoiceStatusFilter;
  page: number;
  pageSize: number;
  total: number;
}

/**
 * The invoices list, one page at a time (the server limits requesters to the invoices of their own orders).
 * Stale-while-revalidate like the requests list: asking again within the stale time does nothing, an `invoice` push or a
 * reconnect refreshes the page on screen in the background, moving to another page or filter loads in the foreground.
 */
export const InvoicesStore = signalStore(
  { providedIn: 'root' },
  withEntities<InvoiceDto>(),
  withState<InvoicesState>(() => ({ status: 'all' as InvoiceStatusFilter, page: 1, pageSize: inject(AppConfigService).config().paging.pageSize, total: 0 })),
  withRequestStatus(),
  withComputed(({ total, pageSize }) => ({
    pageCount: computed(() => Math.max(1, Math.ceil(total() / pageSize()))),
  })),
  withCache(),
  withMethods((store) => {
    const api = inject(InvoicesService);
    const log = inject(Logger).withSource('InvoicesStore');
    let latest = 0;
    let shown: { key: string; epoch: number } | null = null;
    let currentKey: string | null = null;

    const methods = {
      /** Shows one page (1-based). Only the newest call may change the state; a failure sets `error` and keeps the rows. */
      async load(page: number): Promise<void> {
        const pageSize = store.pageSize();
        const status = store.status();
        const key = `list:${page}:${pageSize}:${status}`;
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
              const result = await api.listInvoices({ page, pageSize, ...(status !== 'all' ? { status } : {}) });
              if (call !== latest || epoch !== store.cacheEpoch()) {
                return;
              }
              patchState(store, setAllEntities(result.items), { page: result.page, pageSize: result.pageSize, total: result.total });
              shown = { key, epoch };
              store.setLoaded();
            } catch (error) {
              if (call === latest && epoch === store.cacheEpoch()) {
                log.error('Loading the invoices failed', error);
                store.setError(error, 'The invoices could not be loaded.');
              }
              throw error;
            }
          });
        } catch {
          // Already recorded in `error`.
        }
      },

      /** Changes the status filter and shows page 1 of the result. */
      async setStatus(status: InvoiceStatusFilter): Promise<void> {
        patchState(store, { status });
        await methods.load(1);
      },
    };
    return methods;
  }),
  withHooks((store) => {
    let off: (() => void) | null = null;
    return {
      onInit() {
        off = inject(LiveConnection).onEntityChanged('invoice', () => store.invalidate('list'));
      },
      onDestroy() {
        off?.();
      },
    };
  }),
);
