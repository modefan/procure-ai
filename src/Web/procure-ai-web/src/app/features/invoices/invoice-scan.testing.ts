import { InvoiceScanDto } from '../../api/assistant/models/invoice-scan-dto';

/** The demo layout the Fake reader understands: 3 lines, R 86 940,00 incl. VAT, high confidence. */
export const scanDto = (o: Partial<InvoiceScanDto> = {}): InvoiceScanDto => ({
  id: 'scan-1',
  confidence: 'High',
  invoiceNumber: 'INV-5530',
  invoiceDate: '2026-10-28',
  orderNumber: 'PO-881',
  supplierName: 'Contoso Hardware',
  supplierVatNumber: '4123456789',
  lines: [
    { description: 'Laptop 14"', quantity: 3, unitPriceExclVat: 25200, lineTotalExclVat: 75600 },
    { description: 'Wireless mouse', quantity: 1, unitPriceExclVat: 100, lineTotalExclVat: 100 },
    { description: 'Delivery', quantity: 1, unitPriceExclVat: 0, lineTotalExclVat: 0 },
  ],
  totalExclVat: 75700,
  vat: 11355,
  totalInclVat: 86940,
  warnings: [],
  model: 'fake',
  ...o,
});

export const wrongTotalWarning = { code: 'TotalMismatch', message: 'The stated total R 86 940,00 does not match the lines: they add up to R 87 055,00 incl. VAT.' };
