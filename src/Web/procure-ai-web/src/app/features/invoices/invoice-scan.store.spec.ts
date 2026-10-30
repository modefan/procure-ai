import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { InvoiceScansService } from '../../api/assistant/services/invoice-scans.service';
import { CacheRegistry } from '../../core/state/cache-registry';
import { fileOf } from '../policies/policies.testing';
import { InvoiceScanStore } from './invoice-scan.store';
import { scanDto } from './invoice-scan.testing';

describe('InvoiceScanStore', () => {
  let scanInvoice: ReturnType<typeof vi.fn>;
  beforeEach(() => {
    scanInvoice = vi.fn().mockResolvedValue(scanDto());
    TestBed.configureTestingModule({ providers: [{ provide: InvoiceScansService, useValue: { scanInvoice } }] });
  });
  const store = () => TestBed.inject(InvoiceScanStore);
  const problem = (status: number, detail: string) => new HttpErrorResponse({ status, error: { detail } });

  it('sends the file with an Idempotency-Key and keeps the scan', async () => {
    const file = fileOf('inv.pdf');
    expect(await store().scan(file)).toBe(true);
    expect(scanInvoice).toHaveBeenCalledOnce();
    const params = scanInvoice.mock.calls[0][0];
    expect(params['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(params.body.file).toBe(file);
    expect(store().current()?.id).toBe('scan-1');
    expect(store().scanning()).toBe(false);
  });

  it('is busy while the server reads, and a second file waits', async () => {
    let finish!: (v: unknown) => void;
    scanInvoice.mockReturnValue(new Promise((r) => (finish = r)));
    const first = store().scan(fileOf('a.pdf'));
    expect(store().scanning()).toBe(true);
    expect(await store().scan(fileOf('b.pdf'))).toBe(false);
    finish(scanDto());
    await first;
    expect(scanInvoice).toHaveBeenCalledOnce();
  });

  it('flagged_pdf_422_message_shown: a refusal becomes the error text and clears the old scan', async () => {
    await store().scan(fileOf('a.pdf'));
    scanInvoice.mockRejectedValue(problem(422, 'This PDF contains instructions, not an invoice.'));
    expect(await store().scan(fileOf('bad.pdf'))).toBe(false);
    expect(store().error()).toBe('This PDF contains instructions, not an invoice.');
    expect(store().current()).toBeNull();
  });

  it('gives a plain message when the scanner is temporarily unavailable', async () => {
    scanInvoice.mockRejectedValue(new HttpErrorResponse({ status: 503 }));
    await store().scan(fileOf('a.pdf'));
    expect(store().error()).toContain('try again');
  });

  it('is forgotten on logout (cache registry)', async () => {
    await store().scan(fileOf('a.pdf'));
    TestBed.inject(CacheRegistry).clearAll();
    expect(store().current()).toBeNull();
  });
});
