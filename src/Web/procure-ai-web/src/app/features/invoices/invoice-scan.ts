import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { formatRand, keepRand } from '../../core/ui/money.pipe';
import { UploadDropZone } from '../../core/ui/upload-drop-zone';
import { InvoiceScanStore } from './invoice-scan.store';

/** The 10 MB limit the Assistant service enforces for uploads; the drop zone checks it before sending. */
export const SCAN_MAX_BYTES = 10 * 1024 * 1024;

/**
 * The scan panel of screen 10 (needs `invoices.create`; the page only shows it then). Drop a PDF: the AI reads supplier,
 * lines, VAT and totals. The result is a summary line, any warnings, and "Review and save", which opens the entry form
 * prefilled. Nothing is saved here: the user always confirms in the form.
 */
@Component({
  selector: 'app-invoice-scan-panel',
  imports: [UploadDropZone],
  template: `
    <div class="card space-y-4">
      <div>
        <h2 class="text-base font-semibold text-slate-900">Scan an invoice</h2>
        <p class="mt-1 text-sm text-slate-600">The AI reads supplier, lines, VAT and totals. You check everything before it is saved.</p>
      </div>
      <app-upload-drop-zone class="block" accept=".pdf" [maxBytes]="maxBytes" [busy]="store.scanning()" busyLabel="Reading the invoice…" (fileSelected)="store.scan($event)">
        <span aria-hidden="true">📄</span> Drop an invoice PDF here
      </app-upload-drop-zone>

      @if (store.error(); as message) {
        <p role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm break-words text-red-900" data-testid="scan-error">{{ message }}</p>
      }

      @if (store.current(); as scan) {
        <div class="space-y-3" data-testid="scan-result">
          <p class="text-sm text-slate-800" data-testid="scan-summary">
            <span class="font-semibold">Scanned: {{ scan.invoiceNumber }}</span>
            · {{ scan.supplierName }} · {{ scan.lines.length }} {{ scan.lines.length === 1 ? 'line' : 'lines' }} ·
            <span class="whitespace-nowrap">{{ total(scan.totalInclVat) }} incl. VAT</span> ·
            <span class="inline-block rounded-full px-2 py-0.5 text-xs font-semibold whitespace-nowrap" [class]="confidenceClass(scan.confidence)">confidence {{ scan.confidence.toLowerCase() }}</span>
          </p>
          @if (scan.warnings.length > 0) {
            <ul class="space-y-1 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900" data-testid="scan-warnings" aria-label="Warnings from the scan">
              @for (warning of scan.warnings; track $index) {
                <li class="break-words">{{ keep(warning.message) }}</li>
              }
            </ul>
          }
          <button type="button" class="btn-primary whitespace-nowrap" data-testid="review-scan" (click)="review(scan.id)">Review and save</button>
        </div>
      }
    </div>
  `,
})
export class InvoiceScanPanel {
  protected readonly store = inject(InvoiceScanStore);
  private readonly router = inject(Router);
  protected readonly maxBytes = SCAN_MAX_BYTES;
  protected readonly keep = keepRand;
  protected readonly total = (amount: number) => formatRand(amount);
  protected readonly confidenceClass = (confidence: string) =>
    confidence.toLowerCase() === 'high' ? 'bg-emerald-100 text-emerald-800' : 'bg-amber-100 text-amber-900';

  protected review(id: string): void {
    void this.router.navigate(['/app/invoices/new'], { queryParams: { scan: id } });
  }
}
