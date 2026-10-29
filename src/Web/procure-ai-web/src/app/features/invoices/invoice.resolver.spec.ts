import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, convertToParamMap, provideRouter, RedirectCommand } from '@angular/router';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { invoiceDto } from '../requests/request-detail.testing';
import { invoiceResolver } from './invoice.resolver';

describe('invoiceResolver', () => {
  const run = (getInvoice: ReturnType<typeof vi.fn>) => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: InvoicesService, useValue: { getInvoice } }] });
    const route = { paramMap: convertToParamMap({ id: 'i1' }) } as ActivatedRouteSnapshot;
    return TestBed.runInInjectionContext(() => invoiceResolver(route, {} as never)) as Promise<unknown>;
  };

  it('resolves the invoice', async () => {
    const dto = invoiceDto();
    expect(await run(vi.fn().mockResolvedValue(dto))).toBe(dto);
  });

  it('sends a 404 to the not-found page', async () => {
    const result = await run(vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 404 })));
    expect(result).toBeInstanceOf(RedirectCommand);
  });

  it('any other failure resolves to null so the page shows its own error', async () => {
    expect(await run(vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 500 })))).toBeNull();
  });
});
