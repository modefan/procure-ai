import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { InvoiceScansService } from '../../api/assistant/services/invoice-scans.service';
import { fileOf } from '../policies/policies.testing';
import { InvoiceScanPanel } from './invoice-scan';
import { scanDto, wrongTotalWarning } from './invoice-scan.testing';

describe('InvoiceScanPanel', () => {
  let scanInvoice: ReturnType<typeof vi.fn>;
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function setup(scan = scanDto()) {
    scanInvoice = vi.fn().mockResolvedValue(scan);
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: InvoiceScansService, useValue: { scanInvoice } }] });
    const fixture = TestBed.createComponent(InvoiceScanPanel);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const drop = async (name = 'inv.pdf') => {
      fixture.debugElement.query((d) => d.name === 'app-upload-drop-zone').componentInstance.fileSelected.emit(fileOf(name));
      for (let i = 0; i < 4; i++) {
        await fixture.whenStable();
        await new Promise((r) => setTimeout(r));
        fixture.detectChanges();
      }
    };
    return { fixture, el, drop };
  }

  it('scan_summary_shows_confidence_and_total', async () => {
    const { el, drop } = await setup();
    await drop();
    const summary = text(el.querySelector('[data-testid="scan-summary"]'));
    expect(summary).toContain('Scanned: INV-5530');
    expect(summary).toContain('Contoso Hardware');
    expect(summary).toContain('3 lines');
    expect(summary).toContain('R 86 940,00 incl. VAT');
    expect(summary.toLowerCase()).toContain('confidence high');
  });

  it('Review and save opens the form with the scan id', async () => {
    const { el, drop } = await setup();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await drop();
    (el.querySelector('[data-testid="review-scan"]') as HTMLElement).click();
    expect(navigate).toHaveBeenCalledWith(['/app/invoices/new'], { queryParams: { scan: 'scan-1' } });
  });

  it('low confidence lists the warnings as the server worded them', async () => {
    const { el, drop } = await setup(scanDto({ confidence: 'Low', warnings: [wrongTotalWarning] }));
    await drop();
    expect(text(el.querySelector('[data-testid="scan-summary"]')).toLowerCase()).toContain('confidence low');
    expect(text(el.querySelector('[data-testid="scan-warnings"]'))).toContain(wrongTotalWarning.message);
  });

  it('flagged_pdf_422_message_shown', async () => {
    const { el, drop, fixture } = await setup();
    const { HttpErrorResponse } = await import('@angular/common/http');
    scanInvoice.mockRejectedValue(new HttpErrorResponse({ status: 422, error: { detail: 'This PDF contains instructions, not an invoice.' } }));
    await drop('bad.pdf');
    fixture.detectChanges();
    expect(text(el.querySelector('[role="alert"]'))).toContain('This PDF contains instructions, not an invoice.');
    expect(el.querySelector('[data-testid="review-scan"]')).toBeNull();
  });
});
