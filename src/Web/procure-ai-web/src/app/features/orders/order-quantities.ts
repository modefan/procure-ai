import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';

const sum = (values: Iterable<number>) => [...values].reduce((a, b) => a + b, 0);

/** Units ordered over all lines. */
export const orderedTotal = (order: PurchaseOrderDto): number => sum(order.lines.map((l) => l.ordered));
/** Units received over all lines. */
export const receivedTotal = (order: PurchaseOrderDto): number => sum(order.lines.map((l) => l.received));
/** Units still to be delivered (not received, not closed short). */
export const outstandingTotal = (order: PurchaseOrderDto): number => sum(order.lines.map((l) => l.outstanding));

/** "2 of 3": units received of units ordered. */
export const receivedText = (order: PurchaseOrderDto): string => `${receivedTotal(order)} of ${orderedTotal(order)}`;

/** Goods can be recorded once the supplier confirmed, while something is outstanding. */
export const canReceive = (order: PurchaseOrderDto): boolean =>
  (order.status === 'Confirmed' || order.status === 'PartiallyReceived') && outstandingTotal(order) > 0;

/** An order can be closed short after at least one receipt, while something is still outstanding. */
export const canCloseShort = (order: PurchaseOrderDto): boolean =>
  order.status === 'PartiallyReceived' && order.receipts.length > 0 && outstandingTotal(order) > 0;

/** Invoiced and paid units per order line number, over all the order's invoices. */
export function invoicedAndPaid(invoices: readonly InvoiceDto[]): Map<number, { invoiced: number; paid: number }> {
  const result = new Map<number, { invoiced: number; paid: number }>();
  for (const invoice of invoices) {
    for (const line of invoice.lines) {
      const total = result.get(line.lineNo) ?? { invoiced: 0, paid: 0 };
      result.set(line.lineNo, { invoiced: total.invoiced + line.quantity, paid: total.paid + line.paid });
    }
  }
  return result;
}
