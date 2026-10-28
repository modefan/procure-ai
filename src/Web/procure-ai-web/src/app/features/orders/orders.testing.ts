import { OrderLineDto } from '../../api/purchasing/models/order-line-dto';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { orderDto } from '../requests/request-detail.testing';

/** Fixtures for the orders specs. */
export const orderLine = (lineNo: number, ordered: number, received = 0, o: Partial<OrderLineDto> = {}): OrderLineDto => ({
  lineNo, sku: `SKU-${lineNo}`, description: `Item ${lineNo}`, ordered, received, outstanding: ordered - received - (o.closedShort ?? 0), closedShort: 0,
  unitPriceExclVat: 100, unitPriceExclVatDisplay: 'R 100,00', totalExclVat: ordered * 100, totalExclVatDisplay: `R ${ordered * 100},00`, ...o,
});

/** An order with 3 units ordered (2 + 1), `received` of them delivered (first line first). */
export const order = (o: Partial<PurchaseOrderDto> = {}): PurchaseOrderDto => orderDto({ lines: [orderLine(1, 2), orderLine(2, 1)], ...o });

export const ordersPage = (items: PurchaseOrderDto[], page = 1, total = items.length, pageSize = 25) => ({ items, page, pageSize, total });
