import { ApprovalStepDto } from '../../api/approvals/models/approval-step-dto';

/** Fixture for the inbox specs: an open step the caller may act on. */
export const inboxStep = (o: Partial<ApprovalStepDto> = {}): ApprovalStepDto => ({
  id: 's1', requestId: 'r1', requestNumber: 'PR-1044', requestTitle: 'Design software licences', requesterId: 'ana', stepNo: 1, level: 'Manager',
  status: 'Pending', canAct: true, cannotActReason: null, dueAt: '2026-10-30T09:00:00Z', totalInclVat: 49680, totalInclVatDisplay: 'R 49 680,00', version: 1730, ...o,
});

export const inboxPage = (items: ApprovalStepDto[], page = 1, total = items.length, pageSize = 25) => ({ items, page, pageSize, total });
