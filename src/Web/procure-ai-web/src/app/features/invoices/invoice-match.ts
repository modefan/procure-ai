import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { InvoiceLineDto } from '../../api/invoicing/models/invoice-line-dto';
import { formatRand } from '../../core/ui/money.pipe';

/** What the match badge shows. A paid invoice keeps its mismatch: the match is read from the reasons, not the status. */
export type MatchStatus = 'AwaitingOrder' | 'Matched' | 'Mismatched';
export type PaymentStatus = 'Unpaid' | 'Held' | 'PartiallyPaid' | 'Paid';

export function matchStatus(invoice: InvoiceDto): MatchStatus {
  if (invoice.status === 'AwaitingOrder') {
    return 'AwaitingOrder';
  }
  return invoice.mismatchReasons.length > 0 || invoice.status === 'Mismatched' ? 'Mismatched' : 'Matched';
}

export function paymentStatus(invoice: InvoiceDto): PaymentStatus {
  if (invoice.status === 'Paid') {
    return 'Paid';
  }
  if (invoice.onHold) {
    return 'Held';
  }
  return invoice.status === 'PartiallyPaid' ? 'PartiallyPaid' : 'Unpaid';
}

/**
 * The result column of the match (wireframe 11): what is wrong with this line in a few words, else "Matched".
 * The numbers come from the server's match; the wording is ours, so it stays short ("Invoiced 1 more than received").
 */
export function lineResult(line: InvoiceLineDto): string {
  if (line.quantityOk === null && line.priceOk === null) {
    return 'Waiting for the order';
  }
  if (line.ordered === null && line.orderUnitPriceExclVat === null && line.matchReason) {
    return line.matchReason; // the line is not on the order
  }
  const problems: string[] = [];
  if (line.quantityOk === false) {
    const over = (line.invoicedBefore ?? 0) + line.quantity - (line.received ?? 0);
    problems.push(`Invoiced ${over} more than received`);
  }
  if (line.priceOk === false && line.orderUnitPriceExclVat !== null) {
    problems.push(`price differs by ${formatRand(Math.abs(line.unitPriceExclVat - line.orderUnitPriceExclVat))} per unit`);
  }
  if (problems.length === 0) {
    return 'Matched';
  }
  const text = problems.join('; ');
  return text.charAt(0).toUpperCase() + text.slice(1);
}

export const isLineProblem = (line: InvoiceLineDto): boolean => line.quantityOk === false || line.priceOk === false;

/** Pay-all would leave part of the invoice unpaid (something is invoiced but not payable yet). */
export function isPartialPayment(invoice: InvoiceDto): boolean {
  return invoice.lines.some((l) => l.payable < l.quantity - l.paid);
}

/** The pay button: `Approve partial payment (R 57 960,00)` or `Pay invoice (R 86 940,00)`. */
export function payAllLabel(invoice: InvoiceDto): string {
  return `${isPartialPayment(invoice) ? 'Approve partial payment' : 'Pay invoice'} (${invoice.payableInclVatText})`;
}
