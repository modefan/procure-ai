import { formatRand } from '../../core/ui/money.pipe';
import { invoiceTotals } from './invoice-totals';
import { isPartialPayment, lineResult, matchStatus, payAllLabel, paymentStatus } from './invoice-match';
import { invoiceLine, mismatched } from './invoices.testing';
import { invoiceDto } from '../requests/request-detail.testing';

describe('invoice match texts', () => {
  it('line_result_texts: [over-received, price over tolerance, matched]', () => {
    const overReceived = invoiceLine(1, { quantity: 3, received: 2, quantityOk: false });
    const priceOver = invoiceLine(2, { unitPriceExclVat: 25300, priceOk: false });
    const matched = invoiceLine(3);
    expect([overReceived, priceOver, matched].map(lineResult)).toEqual([
      'Invoiced 1 more than received',
      `Price differs by ${formatRand(100)} per unit`,
      'Matched',
    ]);
  });

  it('counts earlier invoices when saying how much more was invoiced than received', () => {
    expect(lineResult(invoiceLine(1, { quantity: 2, received: 3, invoicedBefore: 2, quantityOk: false }))).toBe('Invoiced 1 more than received');
  });

  it('says both problems when quantity and price are off, and uses the server text for a line not on the order', () => {
    expect(lineResult(invoiceLine(1, { quantity: 3, received: 2, quantityOk: false, unitPriceExclVat: 25000, priceOk: false }))).toBe(
      `Invoiced 1 more than received; price differs by ${formatRand(200)} per unit`,
    );
    expect(lineResult(invoiceLine(9, { ordered: null, orderUnitPriceExclVat: null, priceOk: false, quantityOk: false, matchReason: 'Line 9 is not on PO-881' }))).toBe('Line 9 is not on PO-881');
    expect(lineResult(invoiceLine(1, { priceOk: null, quantityOk: null, ordered: null, received: null, orderUnitPriceExclVat: null }))).toBe('Waiting for the order');
  });

  it('match and payment status are separate: a paid invoice keeps its mismatch', () => {
    expect(matchStatus(invoiceDto({ status: 'AwaitingOrder' }))).toBe('AwaitingOrder');
    expect(matchStatus(invoiceDto({ status: 'Matched' }))).toBe('Matched');
    expect(matchStatus(mismatched({ status: 'PartiallyPaid' }))).toBe('Mismatched');
    expect(matchStatus(invoiceDto({ status: 'Paid' }))).toBe('Matched');
    expect(paymentStatus(invoiceDto({ status: 'Matched' }))).toBe('Unpaid');
    expect(paymentStatus(invoiceDto({ status: 'PartiallyPaid' }))).toBe('PartiallyPaid');
    expect(paymentStatus(invoiceDto({ status: 'Paid' }))).toBe('Paid');
    expect(paymentStatus(invoiceDto({ status: 'Matched', onHold: true }))).toBe('Held');
  });

  it('a payment that leaves something unpaid is a partial payment', () => {
    expect(isPartialPayment(mismatched())).toBe(true); // 3 invoiced, 2 payable
    expect(isPartialPayment(invoiceDto({ lines: [invoiceLine(1)] }))).toBe(false);
    expect(payAllLabel(mismatched())).toBe(`Approve partial payment (${mismatched().payableInclVatText})`);
    expect(payAllLabel(invoiceDto({ lines: [invoiceLine(1)], payableInclVat: 86940, payableInclVatText: 'R 86 940,00' }))).toBe('Pay invoice (R 86 940,00)');
  });
});

describe('invoiceTotals', () => {
  it('manual_entry_vat_on_total: VAT is worked out once on the total, not per line', () => {
    // 2 lines of R 10,10: per-line VAT would be 1,52 + 1,52 = 3,04; on the total R 20,20 it is 3,03.
    const totals = invoiceTotals([{ quantity: 1, unitPriceExclVat: 10.1 }, { quantity: 1, unitPriceExclVat: 10.1 }]);
    expect(totals).toEqual({ exclVat: 20.2, vat: 3.03, inclVat: 23.23 });
  });

  it('works in cents so sums never drift, and skips lines without a number', () => {
    expect(invoiceTotals([{ quantity: 3, unitPriceExclVat: 0.1 }, { quantity: Number.NaN, unitPriceExclVat: 5 }])).toEqual({ exclVat: 0.3, vat: 0.05, inclVat: 0.35 });
    expect(invoiceTotals([{ quantity: 3, unitPriceExclVat: 25200 }])).toEqual({ exclVat: 75600, vat: 11340, inclVat: 86940 });
  });
});
