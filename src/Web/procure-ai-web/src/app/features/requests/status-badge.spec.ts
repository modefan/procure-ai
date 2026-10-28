import { TestBed } from '@angular/core/testing';
import { StatusBadge, statusGroup, StatusGroup } from './status-badge';
import { RequestStatus } from '../../api/requests/models/request-status';
import { OrderStatus } from '../../api/purchasing/models/order-status';
import { InvoiceStatus } from '../../api/invoicing/models/invoice-status';
import { ExportStatus } from '../../api/invoicing/models/export-status';

// Every value of every generated status type, written out so a new backend status makes the compiler complain here.
const requests: Record<RequestStatus, StatusGroup> = {
  Draft: 'neutral', Submitted: 'info', Approved: 'ok', Ordered: 'info', Completed: 'ok', Rejected: 'bad', Cancelling: 'warn', Cancelled: 'bad',
};
const orders: Record<OrderStatus, StatusGroup> = {
  PendingSupplier: 'warn', SentToSupplier: 'info', Confirmed: 'info', Rejected: 'bad', PartiallyReceived: 'info', Received: 'ok', ClosedShort: 'warn', Cancelled: 'bad',
};
const invoices: Record<InvoiceStatus, StatusGroup> = {
  AwaitingOrder: 'warn', Matched: 'ok', Mismatched: 'warn', PartiallyPaid: 'info', Paid: 'ok',
};
const exports_: Record<ExportStatus, StatusGroup> = { Pending: 'neutral', Exported: 'ok' };
// Approval steps come as plain strings from the Approvals API (ApprovalStepStatus, plus Merged for a step folded into another).
const steps: Record<string, StatusGroup> = { Pending: 'neutral', Approved: 'ok', Rejected: 'bad', Escalated: 'warn', Cancelled: 'bad', Merged: 'neutral' };
// The supplier side of an order is one line of text from Purchasing (SupplierStatusText).
const supplier: Record<string, StatusGroup> = {
  'Waiting for supplier': 'warn',
  'Supplier unavailable · retrying (attempt 3)': 'warn',
  'Sent to supplier, waiting for confirmation': 'info',
  'Rejected by supplier': 'bad',
  'Rejected by supplier: out of stock': 'bad',
  'Cancelled, telling the supplier': 'warn',
  Cancelled: 'bad',
  'Confirmed, delivery 2026-11-03': 'ok',
};

describe('statusGroup', () => {
  it.each(Object.entries({ ...requests, ...orders, ...invoices, ...exports_, ...steps }))('%s has a colour group', (status, group) => {
    expect(statusGroup(status)).toBe(group);
  });

  it.each(Object.entries(supplier))('supplier text "%s"', (text, group) => {
    expect(statusGroup(text)).toBe(group);
  });

  it('the generic words are grouped as the brief says', () => {
    expect(statusGroup('Failed')).toBe('bad');
    expect(statusGroup('Retrying')).toBe('warn');
    expect(statusGroup('Held')).toBe('warn');
    expect(statusGroup('Waiting')).toBe('warn');
    expect(statusGroup('InProgress')).toBe('info');
  });

  it('unknown statuses are neutral', () => {
    expect(statusGroup('SomethingNew')).toBe('neutral');
    expect(statusGroup('')).toBe('neutral');
  });
});

describe('StatusBadge', () => {
  function render(status: string) {
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.componentRef.setInput('status', status);
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('span') as HTMLElement;
  }

  it('shows the status as words on one line, coloured by its group', () => {
    const span = render('PartiallyReceived');
    expect(span.textContent!.trim()).toBe('Partially received');
    expect(span.className).toContain('whitespace-nowrap');
    expect(span.className).toContain('sky');
  });

  it('keeps single words and sentences as they are', () => {
    expect(render('Draft').textContent!.trim()).toBe('Draft');
    expect(render('Waiting for supplier').textContent!.trim()).toBe('Waiting for supplier');
  });

  it('uses a different colour per group and neutral for unknown values', () => {
    const classes = ['Draft', 'Submitted', 'Cancelling', 'Paid', 'Rejected'].map((s) => render(s).className);
    expect(new Set(classes).size).toBe(5);
    expect(render('???').className).toBe(render('Draft').className);
  });
});
