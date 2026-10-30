import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { InvoiceScanDto } from '../../api/assistant/models/invoice-scan-dto';
import { InvoiceScansService } from '../../api/assistant/services/invoice-scans.service';
import { idempotencyKey } from '../../core/http/idempotency';
import { problemOf } from '../../core/http/problem-fields';
import { Logger } from '../../core/logging/logger';
import { withCache } from '../../core/state/with-cache';

interface ScanState {
  /** The last scan the server returned; the form reads it from here (`?scan=<id>`). Null until one is made. */
  current: InvoiceScanDto | null;
  /** True while the server reads a PDF. */
  scanning: boolean;
  /** The server's refusal of the last file (flagged PDF 422, wrong type 415 ...) or a plain "try again"; null otherwise. */
  error: string | null;
}

const BUSY = 'The scanner is busy right now. Please try again in a moment.';
const FAILED = 'The invoice could not be read. Please try again, or enter it by hand.';

/**
 * Invoice scanning (screen 10, `invoices.create`). `scan(file)` sends the PDF to the Assistant and keeps the result in
 * memory only (never localStorage) until it is saved, replaced, or the user logs out or switches tenant (`withCache()`
 * registers the store, whose `reset()` empties it). A scan waits for the server; a second file while one runs is ignored.
 * Every file gets its own `Idempotency-Key`.
 */
export const InvoiceScanStore = signalStore(
  { providedIn: 'root' },
  withState<ScanState>({ current: null, scanning: false, error: null }),
  withCache(),
  withMethods((store) => {
    const api = inject(InvoiceScansService);
    const log = inject(Logger).withSource('InvoiceScanStore');
    return {
      /** Reads the PDF. Resolves true when there is a scan to review. */
      async scan(file: File): Promise<boolean> {
        if (store.scanning()) {
          return false;
        }
        const epoch = store.cacheEpoch();
        patchState(store, { scanning: true, error: null, current: null });
        try {
          const result = await api.scanInvoice({ 'Idempotency-Key': idempotencyKey(), body: { file } });
          if (epoch !== store.cacheEpoch()) {
            return false; // logged out meanwhile: the answer is not shown to the next user
          }
          patchState(store, { current: result });
          return true;
        } catch (error) {
          if (epoch !== store.cacheEpoch()) {
            return false;
          }
          log.warn('Scanning the invoice failed', error);
          const status = error instanceof HttpErrorResponse ? error.status : 0;
          patchState(store, { error: problemOf(error).general ?? (status === 503 ? BUSY : FAILED) });
          return false;
        } finally {
          if (epoch === store.cacheEpoch()) {
            patchState(store, { scanning: false });
          }
        }
      },

      /** Forgets the scan, e.g. once its invoice is saved. */
      clear(): void {
        patchState(store, { current: null, error: null });
      },
    };
  }),
);

export type InvoiceScanStore = InstanceType<typeof InvoiceScanStore>;
