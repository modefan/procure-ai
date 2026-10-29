import { ApprovalChainDto } from '../../api/approvals/models/approval-chain-dto';
import { ApprovalStepDto } from '../../api/approvals/models/approval-step-dto';
import { BusinessEventDto } from '../../api/audit/models/business-event-dto';
import { InvoiceDto } from '../../api/invoicing/models/invoice-dto';
import { PurchaseOrderDto } from '../../api/purchasing/models/purchase-order-dto';
import { RequestDto } from '../../api/requests/models/request-dto';

/** Fixtures for the request detail specs. */
export const requestDto = (o: Partial<RequestDto> = {}): RequestDto => ({
  id: 'r1', number: 'PR-1043', title: '3 laptops for new hires', status: 'Submitted', costCenterCode: 'IT-200', supplierId: 'CH', requesterId: 'tom',
  lines: [{ lineNo: 1, sku: 'L14', description: 'Laptop 14"', quantity: 3, unitPriceExclVat: 25200, unitPriceExclVatDisplay: 'R 25 200,00', totalExclVat: 75600, totalExclVatDisplay: 'R 75 600,00' }],
  submittedAt: '2026-10-28T09:02:00Z', approvedAt: null, rejectedAt: null, rejectionReason: null, cancelledAt: null,
  cancellationReason: null, orderId: null, orderNumber: null, spentInclVat: null, spentInclVatDisplay: null, cancellationRefusedReason: null,
  totalExclVat: 75600, totalExclVatDisplay: 'R 75 600,00', vat: 11340, vatDisplay: 'R 11 340,00', totalInclVat: 86940, totalInclVatDisplay: 'R 86 940,00',
  version: 1730, ...o,
});

export const stepDto = (o: Partial<ApprovalStepDto> = {}): ApprovalStepDto => ({
  id: 's1', requestId: 'r1', requestNumber: 'PR-1043', requestTitle: '3 laptops', requesterId: 'tom', stepNo: 1, level: 'Manager', status: 'Pending',
  canAct: false, cannotActReason: null, dueAt: '2026-10-30T09:02:00Z', totalInclVat: 86940, totalInclVatDisplay: 'R 86 940,00', version: 1, ...o,
});

export const chainDto = (state: string, steps: ApprovalStepDto[]): ApprovalChainDto => ({ requestId: 'r1', state, steps });

export const eventDto = (type: string, summary: string, at = '2026-10-28T09:02:00Z', userId = 'tom', extra: Partial<BusinessEventDto> = {}): BusinessEventDto => ({
  type, summary, occurredAt: at, userId, ...extra,
});

export const orderDto = (o: Partial<PurchaseOrderDto> = {}): PurchaseOrderDto => ({
  id: 'o1', number: 'PO-881', requestId: 'r1', requestNumber: 'PR-1043', requesterId: 'tom', costCenterCode: 'IT-200', supplierId: 'CH', status: 'Confirmed',
  supplierStatus: 'Confirmed, reference SUP-1', supplierReference: 'SUP-1', supplierAttempts: 1, supplierLastError: null, createdAt: '2026-10-28T10:00:00Z',
  expectedDelivery: null, cancelReason: null, cancelledAt: null, receipts: [],
  lines: [{ lineNo: 1, sku: 'L14', description: 'Laptop 14"', ordered: 3, received: 0, outstanding: 3, closedShort: 0, unitPriceExclVat: 25200, unitPriceExclVatDisplay: 'R 25 200,00', totalExclVat: 75600, totalExclVatDisplay: 'R 75 600,00' }],
  totalExclVat: 75600, totalExclVatDisplay: 'R 75 600,00', vat: 11340, vatDisplay: 'R 11 340,00', totalInclVat: 86940, totalInclVatDisplay: 'R 86 940,00', version: 1, ...o,
});

export const invoiceDto = (o: Partial<InvoiceDto> = {}): InvoiceDto => ({
  id: 'i1', orderId: 'o1', orderNumber: 'PO-881', requestId: 'r1', sequence: 1, supplierId: 'CH', supplierInvoiceNumber: 'INV-77', source: 'Webhook',
  status: 'Matched', holdReason: null, onHold: false, mismatchReasons: [], payments: [], lines: [], invoiceDate: '2026-10-29', receivedAt: '2026-10-29T08:00:00Z',
  totalExclVat: 75600, totalExclVatText: 'R 75 600,00', vat: 11340, vatText: 'R 11 340,00', totalInclVat: 86940, totalInclVatText: 'R 86 940,00', payableInclVat: 0, payableInclVatText: 'R 0,00', version: 1, ...o,
});
