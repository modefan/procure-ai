import { canCloseShort, canReceive, invoicedAndPaid, receivedText } from './order-quantities';
import { order, orderLine } from './orders.testing';
import { invoiceDto } from '../requests/request-detail.testing';

describe('order quantities', () => {
  it('received_shows_x_of_y: the sum of received over the sum of ordered', () => {
    expect(receivedText(order({ lines: [orderLine(1, 2, 2), orderLine(2, 1, 0)] }))).toBe('2 of 3');
    expect(receivedText(order({ lines: [orderLine(1, 6)] }))).toBe('0 of 6');
  });

  it('can receive only a confirmed or partly received order with something outstanding', () => {
    expect(canReceive(order({ status: 'Confirmed' }))).toBe(true);
    expect(canReceive(order({ status: 'PartiallyReceived' }))).toBe(true);
    expect(canReceive(order({ status: 'PendingSupplier' }))).toBe(false);
    expect(canReceive(order({ status: 'Received' }))).toBe(false);
    expect(canReceive(order({ status: 'Confirmed', lines: [orderLine(1, 2, 2)] }))).toBe(false);
  });

  it('can close short only after a receipt, while something is outstanding', () => {
    const receipt = { id: 'g1', deliveryNote: null, receivedAt: '2026-10-28T10:00:00Z', receivedBy: 'u', lines: [{ lineNo: 1, quantity: 1 }] };
    expect(canCloseShort(order({ status: 'Confirmed' }))).toBe(false);
    expect(canCloseShort(order({ status: 'PartiallyReceived', receipts: [receipt], lines: [orderLine(1, 2, 1)] }))).toBe(true);
    expect(canCloseShort(order({ status: 'Received', receipts: [receipt] }))).toBe(false);
  });

  it('sums invoiced and paid quantities per order line over all invoices', () => {
    const line = (lineNo: number, quantity: number, paid: number) => ({ lineNo, quantity, paid }) as never;
    const result = invoicedAndPaid([invoiceDto({ lines: [line(1, 1, 1), line(2, 1, 0)] }), invoiceDto({ id: 'i2', lines: [line(1, 1, 0)] })]);
    expect(result.get(1)).toEqual({ invoiced: 2, paid: 1 });
    expect(result.get(2)).toEqual({ invoiced: 1, paid: 0 });
    expect(result.get(3)).toBeUndefined();
  });
});
