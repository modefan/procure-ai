import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { AuditService } from '../../api/audit/services/audit.service';
import { BudgetsService } from '../../api/budgets/services/budgets.service';
import { InvoicesService } from '../../api/invoicing/services/invoices.service';
import { RequestsService } from '../../api/requests/services/requests.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { PresenceStore } from '../../core/live/presence.store';
import { ReferenceDataStore } from '../../core/reference/reference-data.store';
import { inboxPage, inboxStep } from '../approvals/approvals.testing';

export const activity = (n: number) => ({
  occurredAt: `2026-10-28T10:${String(40 + n).padStart(2, '0')}:00Z`, eventType: 'RequestApproved', summary: `PR-10${n} approved`, requestId: `r${n}`, requestNumber: `PR-10${n}`, actorName: 'Maria',
});

export const budget = (code: string, o: Partial<{ amount: number; spent: number; reserved: number }> = {}) => ({
  costCenterCode: code, fiscalYear: '2026', amount: 900000, spent: 558000, reserved: 75600, available: 266400, version: 1, ...o,
});

type Mock = ReturnType<typeof vi.fn>;

export interface DashboardHarness {
  api: { getApprovalInbox: Mock; listRequests: Mock; listBudgets: Mock; listInvoices: Mock; getActivity: Mock; loadMe: Mock };
  fake: ReturnType<typeof fakeHubFactory>;
  live: LiveConnection;
  online: ReturnType<typeof signal<{ userId: string; name: string }[]>>;
}

/** Wires the dashboard's stores to fake APIs and a fake hub. */
export async function setupDashboard(permissions: string[], costCenters = ['IT-200']): Promise<DashboardHarness> {
  const api = {
    getApprovalInbox: vi.fn().mockResolvedValue(
      inboxPage([inboxStep({ dueAt: '2026-10-28T15:00:00Z' }), inboxStep({ id: 's2', dueAt: '2026-10-30T09:00:00Z' }), inboxStep({ id: 's3' })], 1, 3),
    ),
    listRequests: vi.fn().mockResolvedValue({ items: [], page: 1, pageSize: 1, total: 5 }),
    listBudgets: vi.fn().mockResolvedValue([budget('IT-200'), budget('OPS-100', { amount: 100000, spent: 10000, reserved: 0 })]),
    listInvoices: vi.fn().mockResolvedValue({ items: [], page: 1, pageSize: 1, total: 1 }),
    getActivity: vi.fn().mockResolvedValue([activity(1), activity(2)]),
    loadMe: vi.fn().mockResolvedValue(undefined),
  };
  const me = signal({ userId: 'maria', name: 'Maria', roles: ['manager'], approvalLimit: null, costCenters: costCenters.map((code) => ({ code, name: code, departmentCode: 'IT' })) });
  const online = signal<{ userId: string; name: string }[]>([]);
  const fake = fakeHubFactory();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      { provide: ApprovalsService, useValue: { getApprovalInbox: api.getApprovalInbox } },
      { provide: RequestsService, useValue: { listRequests: api.listRequests } },
      { provide: BudgetsService, useValue: { listBudgets: api.listBudgets } },
      { provide: InvoicesService, useValue: { listInvoices: api.listInvoices } },
      { provide: AuditService, useValue: { getActivity: api.getActivity, getRequestHistory: vi.fn().mockResolvedValue([]) } },
      { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId: 'maria', name: 'Maria Naidoo', tenantId: 'acme', roles: [], permissions }) } },
      { provide: ReferenceDataStore, useValue: { me, costCenters: () => me().costCenters, loadMe: api.loadMe } },
      { provide: PresenceStore, useValue: { online } },
      { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
    ],
  });
  const live = TestBed.inject(LiveConnection);
  live.start();
  await vi.waitFor(() => expect(live.state()).toBe('connected'));
  return { api, fake, live, online };
}
