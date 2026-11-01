import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoiceLineDto } from '../../api/invoicing/models/invoice-line-dto';
import { PaymentDto } from '../../api/invoicing/models/payment-dto';
import { invoiceDto } from '../requests/request-detail.testing';

/** Fixtures for the invoices specs. A matched line: 3 ordered at R 25 200,00, 3 received, 3 invoiced. */
export const invoiceLine = (lineNo: number, o: Partial<InvoiceLineDto> = {}): InvoiceLineDto => ({
  lineNo, sku: `SKU-${lineNo}`, description: `Item ${lineNo}`, quantity: 3, paid: 0, unitPriceExclVat: 25200, lineTotalExclVat: 75600,
  unitPriceText: 'R 25 200,00', lineTotalText: 'R 75 600,00', ordered: 3, orderUnitPriceExclVat: 25200, orderUnitPriceText: 'R 25 200,00',
  priceOk: true, quantityOk: true, received: 3, invoicedBefore: 0, matchReason: null, payable: 3, ...o,
});

export const payment = (o: Partial<PaymentDto> = {}): PaymentDto => ({
  id: 'p1', number: 'PAY-0001', lines: [{ lineNo: 1, sku: 'SKU-1', quantity: 2, amountExclVat: 50400, amountText: 'R 50 400,00' }], totalExclVat: 50400, vat: 7560,
  totalInclVat: 57960, totalInclVatText: 'R 57 960,00', releasedBy: 'Thandi Dlamini', releasedAt: '2026-10-29T09:00:00Z', exportStatus: 'Exported',
  erpReference: 'ERP-5521', exportAttempts: 1, lastExportError: null, ...o,
});

/** The wireframe's invoice: 3 laptops invoiced, 2 received, so 2 are payable (R 57 960,00 with VAT). */
export const mismatched = (o: Partial<InvoiceDto> = {}): InvoiceDto =>
  invoiceDto({
    status: 'Mismatched',
    supplierInvoiceNumber: 'INV-5521',
    mismatchReasons: ['Line 1: invoiced 3, but only 2 received and not yet invoiced'],
    lines: [invoiceLine(1, { description: 'Laptop 14"', received: 2, quantityOk: false, payable: 2, matchReason: 'x' })],
    payableInclVat: 57960,
    payableInclVatText: 'R 57 960,00',
    totalExclVat: 75600, vat: 11340, totalInclVat: 86940,
    ...o,
  });

export const invoicesPage = (items: InvoiceDto[], page = 1, total = items.length, pageSize = 25) => ({ items, page, pageSize, total });
