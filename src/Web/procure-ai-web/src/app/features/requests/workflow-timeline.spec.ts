import { buildTimeline, TimelineInput } from './workflow-timeline';
import { chainDto, eventDto, invoiceDto, orderDto, requestDto, stepDto } from './request-detail.testing';

const base = (o: Partial<TimelineInput> = {}): TimelineInput => ({
  request: requestDto(), chain: null, order: null, invoices: [], history: [], canSeeOrder: true, canSeeInvoices: true, ...o,
});
const submitted = eventDto('RequestSubmitted', 'Submitted by Tom Mokoena: 3 laptops (R 86 940,00 incl. VAT, IT-200)');
const reserved = eventDto('BudgetReserved', 'R 86 940,00 reserved on IT-200 (2026)', '2026-10-28T09:03:00Z', 'system');
const outline = (input: TimelineInput) => buildTimeline(input).map((s) => `${s.state}:${s.title}`);
const step = (input: TimelineInput, title: string) => buildTimeline(input).find((s) => s.title === title)!;

describe('buildTimeline', () => {
  it('timeline_for_each_status: draft', () => {
    expect(outline(base({ request: requestDto({ status: 'Draft', submittedAt: null }) }))).toEqual([
      'pending:Submitted', 'pending:Budget reserved', 'pending:Approval', 'pending:Purchase order', 'pending:Delivery', 'pending:Invoice, match and payment',
    ]);
  });

  it('timeline_for_each_status: submitted, budget not yet answered', () => {
    const input = base({ history: [submitted] });
    expect(outline(input).slice(0, 3)).toEqual(['done:Submitted', 'active:Reserving budget', 'pending:Approval']);
    expect(step(input, 'Submitted').detail).toBe('Submitted by Tom Mokoena');
  });

  it('timeline_for_each_status: waiting for the manager, with reminder and escalation note', () => {
    const input = base({
      chain: chainDto('AwaitingApproval', [stepDto()]),
      history: [submitted, reserved, eventDto('ApprovalRequested', 'x'), eventDto('ApprovalReminderDue', 'Reminder 1 sent to Maria (step 1, Manager)', '2026-10-28T12:00:00Z')],
    });
    expect(outline(input)).toEqual([
      'done:Submitted', 'done:Budget reserved', 'active:Manager approval', 'pending:Purchase order', 'pending:Delivery', 'pending:Invoice, match and payment',
    ]);
    const waiting = step(input, 'Manager approval').detail!;
    expect(waiting).toContain('Waiting for approval');
    expect(waiting).toContain('reminder sent');
    expect(waiting).toContain('escalates');
  });

  it('timeline_for_each_status: waiting for the department head after the manager step moved up', () => {
    const input = base({
      chain: chainDto('AwaitingApproval', [stepDto({ id: 's1', status: 'Escalated' }), stepDto({ id: 's2', level: 'DepartmentHead', status: 'Pending' })]),
      history: [submitted, reserved],
    });
    expect(outline(input).slice(2, 4)).toEqual(['warn:Manager approval', 'active:Department head approval']);
    expect(step(input, 'Manager approval').detail).toBe('No answer in time, moved up to Department head');
  });

  it('timeline_for_each_status: approved, purchase order on its way', () => {
    const input = base({
      request: requestDto({ status: 'Approved' }),
      chain: chainDto('Approved', [stepDto({ status: 'Approved' })]),
      history: [submitted, reserved, eventDto('ApprovalStepApproved', 'Approved by Maria Naidoo (step 1, Manager)', '2026-10-28T09:40:00Z', 'maria')],
    });
    expect(outline(input).slice(2, 4)).toEqual(['done:Manager approval', 'active:Purchase order']);
    expect(step(input, 'Manager approval').detail).toBe('Approved by Maria Naidoo');
    expect(step(input, 'Manager approval').at).toBe('2026-10-28T09:40:00Z');
  });

  it('timeline_for_each_status: ordered, supplier retrying', () => {
    const input = base({
      request: requestDto({ status: 'Ordered', orderId: 'o1', orderNumber: 'PO-881' }),
      chain: chainDto('Ordered', [stepDto({ status: 'Approved' })]),
      order: orderDto({ status: 'PendingSupplier', supplierAttempts: 3, supplierLastError: 'Supplier timed out', supplierStatus: 'Supplier unavailable, retrying' }),
      history: [submitted, reserved],
    });
    const po = step(input, 'Purchase order PO-881');
    expect(po.state).toBe('warn');
    expect(po.detail).toContain('Supplier unavailable, retrying');
    expect(po.detail).toContain('attempt 3');
    expect(po.detail).toContain('Supplier timed out');
    expect(step(input, 'Delivery').state).toBe('pending');
  });

  it('timeline_for_each_status: partly received', () => {
    const order = orderDto({
      status: 'PartiallyReceived',
      receipts: [{ id: 'g1', receivedAt: '2026-10-29T08:00:00Z', receivedBy: 'u', deliveryNote: null, lines: [{ lineNo: 1, quantity: 2 }] }],
    });
    order.lines[0] = { ...order.lines[0], received: 2, outstanding: 1 };
    const input = base({ request: requestDto({ status: 'Ordered', orderNumber: 'PO-881' }), chain: chainDto('Ordered', [stepDto({ status: 'Approved' })]), order, history: [submitted, reserved] });
    expect(step(input, 'Purchase order PO-881').state).toBe('done');
    const delivery = step(input, 'Delivery');
    expect(delivery.state).toBe('active');
    expect(delivery.detail).toBe('2 of 3 received');
  });

  it('timeline_for_each_status: completed', () => {
    const order = orderDto({ status: 'Received' });
    order.lines[0] = { ...order.lines[0], received: 3, outstanding: 0 };
    const input = base({
      request: requestDto({ status: 'Completed', orderNumber: 'PO-881', spentInclVatDisplay: 'R 86 940,00' }),
      chain: chainDto('Completed', [stepDto({ status: 'Approved' })]),
      order,
      invoices: [invoiceDto({ status: 'Paid', payments: [{ number: 'PAY-1', exportStatus: 'Exported', erpReference: 'ERP-9', totalInclVatText: 'R 86 940,00' } as never] })],
      history: [submitted, reserved],
    });
    const t = buildTimeline(input);
    expect(t.every((s) => s.state === 'done')).toBe(true);
    expect(t.at(-1)!.title).toBe('Completed');
    expect(t.at(-1)!.detail).toBe('R 86 940,00 spent');
    expect(step(input, 'Invoice INV-77').detail).toContain('PAY-1');
  });

  it('timeline_for_each_status: cancelled, with the reason and the budget released', () => {
    const input = base({
      request: requestDto({ status: 'Cancelled', cancelledAt: '2026-10-28T11:00:00Z', cancellationReason: 'Not needed any more' }),
      chain: chainDto('Cancelled', [stepDto({ status: 'Cancelled' })]),
      history: [submitted, reserved, eventDto('BudgetReleased', 'R 86 940,00 released on IT-200', '2026-10-28T11:00:00Z', 'system')],
    });
    expect(outline(input)).toEqual(['done:Submitted', 'done:Budget reserved', 'pending:Manager approval', 'done:Budget released', 'failed:Cancelled']);
    expect(step(input, 'Cancelled').detail).toBe('Not needed any more');
  });

  it('timeline_for_each_status: rejected because the budget was refused', () => {
    const input = base({
      request: requestDto({ status: 'Rejected', rejectedAt: '2026-10-28T09:03:00Z', rejectionReason: 'Budget refused on IT-200' }),
      history: [submitted, eventDto('BudgetReservationFailed', 'Budget refused on IT-200: not enough budget (requested R 86 940,00, available R 1 000,00)')],
    });
    expect(outline(input)).toEqual(['done:Submitted', 'failed:Budget refused', 'failed:Rejected']);
    expect(step(input, 'Rejected').detail).toBe('Budget refused on IT-200');
    expect(step(input, 'Budget refused').detail).toContain('not enough budget');
  });

  it('a request waiting for cancellation shows it as in progress, and a refusal as a warning', () => {
    const input = base({ request: requestDto({ status: 'Cancelling', cancellationRefusedReason: 'Goods already received' }), history: [submitted, reserved] });
    expect(step(input, 'Cancelling').state).toBe('active');
    expect(step(input, 'Cancellation refused').detail).toBe('Goods already received');
  });

  it('merged_step_shows_covered_by', () => {
    const input = base({
      chain: chainDto('AwaitingApproval', [
        stepDto({ id: 's1', stepNo: 1, level: 'Manager', status: 'Escalated' }),
        stepDto({ id: 's3', stepNo: 1, level: 'DepartmentHead', status: 'Pending' }),
        stepDto({ id: 's2', stepNo: 2, level: 'DepartmentHead', status: 'Cancelled' }),
      ]),
      history: [submitted, reserved],
    });
    const covered = buildTimeline(input).filter((s) => s.state === 'covered');
    expect(covered.map((s) => s.title)).toEqual(['Department head approval']);
    expect(covered[0].detail).toBe('Covered by the Department head step above');
  });

  it('a cancelled step of a cancelled chain is not called merged', () => {
    const input = base({
      request: requestDto({ status: 'Cancelled', cancellationReason: null }),
      chain: chainDto('Cancelled', [stepDto({ status: 'Cancelled' }), stepDto({ id: 's2', stepNo: 2, level: 'Finance', status: 'Cancelled' })]),
      history: [submitted, reserved],
    });
    expect(buildTimeline(input).some((s) => s.state === 'covered')).toBe(false);
  });

  it('without orders.read the purchase order step comes from the request alone', () => {
    const input = base({ request: requestDto({ status: 'Ordered', orderNumber: 'PO-881' }), chain: chainDto('Ordered', [stepDto({ status: 'Approved' })]), canSeeOrder: false, canSeeInvoices: false, history: [submitted, reserved] });
    expect(step(input, 'Purchase order PO-881').detail).toBe('Placed with the supplier');
    expect(step(input, 'Delivery').state).toBe('pending');
  });
});
