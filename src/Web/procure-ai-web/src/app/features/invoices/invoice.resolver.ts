import { HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { RedirectCommand, ResolveFn, Router } from '@angular/router';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';

/**
 * Loads the invoice before the detail page opens. A missing invoice, another tenant's invoice and one the caller's data
 * scope hides all answer 404, and all go to the same not-found page. Any other failure resolves to null: the page opens
 * and shows its own error with a retry button.
 */
export const invoiceResolver: ResolveFn<InvoiceDto | null | RedirectCommand> = async (route) => {
  const api = inject(InvoicesService);
  const router = inject(Router);
  try {
    return await api.getInvoice({ id: route.paramMap.get('id') ?? '' });
  } catch (error) {
    if (error instanceof HttpErrorResponse && error.status === 404) {
      return new RedirectCommand(router.parseUrl('/not-found'), { skipLocationChange: true });
    }
    return null;
  }
};
