import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { AUDIT_LAG_MS } from '../requests/request-detail.store';
import { DashboardStore } from './dashboard.store';
import { DashboardHarness, setupDashboard } from './dashboard.testing';

const ALL = ['requests.read', 'requests.approve', 'budgets.read', 'invoices.read'];

describe('DashboardStore', () => {
  let h: DashboardHarness;
  afterEach(() => {
    vi.useRealTimers();
    void h?.live.stop();
  });

  it('tiles_follow_permissions: a requester gets no approval, budget or invoice tile and no such calls', async () => {
    h = await setupDashboard(['requests.read', 'requests.create']);
    const store = TestBed.inject(DashboardStore);
    await store.load();

    expect(store.tiles()).toEqual({ approvals: false, myOpen: true, budgets: false, mismatched: false, activity: true });
    expect(h.api.getApprovalInbox).not.toHaveBeenCalled();
    expect(h.api.listBudgets).not.toHaveBeenCalled();
    expect(h.api.listInvoices).not.toHaveBeenCalled();
    expect(h.api.listRequests).toHaveBeenCalledWith({ mine: true, status: 'Open', page: 1, pageSize: 1 });
    expect(store.myOpen()).toBe(5);
  });

  it('a manager with every permission gets every tile', async () => {
    h = await setupDashboard(ALL);
    const store = TestBed.inject(DashboardStore);
    await store.load();

    expect(store.tiles()).toEqual({ approvals: true, myOpen: true, budgets: true, mismatched: true, activity: true });
    expect(store.approvalsTotal()).toBe(3);
    expect(store.soonestEscalation()).toBe('2026-10-28T15:00:00Z');
    expect(h.api.listInvoices).toHaveBeenCalledWith({ status: 'Mismatched', page: 1, pageSize: 1 });
    expect(store.mismatched()).toBe(1);
    expect(store.activity()).toHaveLength(2);
  });

  it('budget tiles are the cost centers of me only, with percent used from spent', async () => {
    h = await setupDashboard(ALL, ['IT-200']);
    const store = TestBed.inject(DashboardStore);
    await store.load();

    const tiles = store.budgetTiles();
    expect(tiles.map((t) => t.code)).toEqual(['IT-200']);
    expect(tiles[0]).toMatchObject({ spent: 558000, reserved: 75600, amount: 900000, spentPercent: 62, reservedPercent: 8 });
  });

  it('a user with budget access but no cost center of their own (finance) gets an overview of the budgets', async () => {
    h = await setupDashboard(['requests.read', 'budgets.read'], []);
    const store = TestBed.inject(DashboardStore);
    await store.load();
    expect(store.budgetTiles().map((t) => t.code)).toEqual(['IT-200', 'OPS-100']);
  });

  it('a tile that fails says so and the others still load', async () => {
    h = await setupDashboard(ALL);
    h.api.listInvoices.mockRejectedValue(new HttpErrorResponse({ status: 503 }));
    const store = TestBed.inject(DashboardStore);
    await store.load();

    expect(store.errors()['mismatched']).toBeTruthy();
    expect(store.myOpen()).toBe(5);
    expect(store.activity()).toHaveLength(2);
  });

  it('activity_refreshes_on_entity_changed (now, and once more after the audit lag)', async () => {
    h = await setupDashboard(ALL);
    const store = TestBed.inject(DashboardStore);
    await store.load();
    expect(h.api.getActivity).toHaveBeenCalledTimes(1);
    h.api.getActivity.mockResolvedValue([
      { occurredAt: 't', eventType: 'RequestRejected', summary: 'PR-9 rejected', requestId: 'r9', requestNumber: 'PR-9', actorName: 'Thandi' },
    ]);
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });

    h.fake.hubs[0].emit('EntityChanged', { entity: 'approval', id: 'r9', requestId: 'r9', eventType: 'x', occurredAt: 't' });
    await vi.waitFor(() => expect(store.activity()[0].requestNumber).toBe('PR-9'));
    expect(h.api.getActivity).toHaveBeenCalledTimes(2);

    await vi.advanceTimersByTimeAsync(AUDIT_LAG_MS + 10);
    expect(h.api.getActivity).toHaveBeenCalledTimes(3);
  });

  it('a budget push refreshes budgets, an invoice push the mismatch count, a request push my open requests', async () => {
    h = await setupDashboard(ALL);
    const store = TestBed.inject(DashboardStore);
    await store.load();
    h.fake.hubs[0].emit('EntityChanged', { entity: 'budget', id: 'b', requestId: null, eventType: 'x', occurredAt: 't' });
    await vi.waitFor(() => expect(h.api.listBudgets).toHaveBeenCalledTimes(2));
    expect(h.api.listInvoices).toHaveBeenCalledTimes(1);

    h.fake.hubs[0].emit('EntityChanged', { entity: 'invoice', id: 'i', requestId: 'r1', eventType: 'x', occurredAt: 't' });
    await vi.waitFor(() => expect(h.api.listInvoices).toHaveBeenCalledTimes(2));

    h.fake.hubs[0].emit('EntityChanged', { entity: 'request', id: 'r1', requestId: 'r1', eventType: 'x', occurredAt: 't' });
    await vi.waitFor(() => expect(h.api.listRequests).toHaveBeenCalledTimes(2));
  });
});
