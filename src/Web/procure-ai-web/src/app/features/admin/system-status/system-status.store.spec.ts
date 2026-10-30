import { TestBed } from '@angular/core/testing';
import { SupplierChaosService } from '../../../api/purchasing/services/supplier-chaos.service';
import { SystemStatusService } from '../../../api/notifications/services/system-status.service';
import { serviceStatus, statusDto } from './system-status.testing';
import { SystemStatusStore } from './system-status.store';

describe('SystemStatusStore', () => {
  let status: { getSystemStatus: ReturnType<typeof vi.fn> };
  let chaos: { setSupplierChaos: ReturnType<typeof vi.fn> };
  const store = () => TestBed.inject(SystemStatusStore);

  beforeEach(() => {
    status = { getSystemStatus: vi.fn().mockResolvedValue(statusDto()) };
    chaos = { setSupplierChaos: vi.fn().mockImplementation(async (p) => ({ mode: p.body.mode, restoresAt: p.body.mode === 'Normal' ? null : '2026-10-30T10:05:00Z' })) };
    TestBed.configureTestingModule({
      providers: [
        { provide: SystemStatusService, useValue: status },
        { provide: SupplierChaosService, useValue: chaos },
      ],
    });
  });
  afterEach(() => vi.useRealTimers());

  it('defaults every known counter to 0 and sums the outbox once', async () => {
    status.getSystemStatus.mockResolvedValue(
      statusDto({
        services: [
          serviceStatus('A', { messages: { incoming: 0, outgoing: 2, scheduled: 0, deadLetters: 1, brokerDeadLetters: 4 } }),
          serviceStatus('B', { messages: { incoming: 0, outgoing: 3, scheduled: 0, deadLetters: 0, brokerDeadLetters: 4 } }),
        ],
        counters: { sagas_running: 5 },
      }),
    );
    await store().load();
    expect(store().counter('sagas_running')).toBe(5);
    expect(store().counter('finance_alerts_open')).toBe(0);
    expect(store().outboxBacklog()).toBe(5);
    expect(store().deadLetters()).toBe(1);
    expect(store().brokerDeadLetters()).toBe(4);
  });

  it('splits pgaudit counters and items from the alerts', async () => {
    status.getSystemStatus.mockResolvedValue(
      statusDto({
        counters: { pgaudit_ddl: 2, pgaudit_write: 7, sagas_running: 1 },
        alerts: [
          { kind: 'pgaudit', text: 'postgres in requests: CREATE TABLE x', tenantId: null, at: '2026-10-30T10:00:00Z', link: null },
          { kind: 'outside-app-change', text: 'orders changed', tenantId: 'acme', at: '2026-10-30T10:00:00Z', link: '/app/admin/data-changes?outside=true&table=orders' },
        ],
      }),
    );
    await store().load();
    expect(store().pgauditCounts()).toEqual([
      { name: 'ddl', count: 2 },
      { name: 'write', count: 7 },
    ]);
    expect(store().pgauditItems().length).toBe(1);
    expect(store().alerts().map((a) => a.kind)).toEqual(['outside-app-change']);
  });

  it('restore_now_sets_normal: sends Normal with a fresh idempotency key and shows the answer', async () => {
    status.getSystemStatus.mockResolvedValue(statusDto({ supplierChaos: { mode: 'Failing', restoresAt: '2026-10-30T10:05:00Z' } }));
    await store().load();
    await store().setChaos('Normal');
    expect(chaos.setSupplierChaos).toHaveBeenCalledTimes(1);
    const params = chaos.setSupplierChaos.mock.calls[0][0];
    expect(params.body).toEqual({ mode: 'Normal' });
    expect(params['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(store().chaos()).toEqual({ mode: 'Normal', restoresAt: null });
    await store().setChaos('Failing');
    expect(chaos.setSupplierChaos.mock.calls[1][0]['Idempotency-Key']).not.toBe(params['Idempotency-Key']);
  });

  it('a failed chaos call is reported and does not change the mode', async () => {
    await store().load();
    chaos.setSupplierChaos.mockRejectedValue(new Error('boom'));
    await store().setChaos('Failing');
    expect(store().chaosError()).toBeTruthy();
    expect(store().chaos()).toBeNull();
  });

  it('refresh_pauses_when_hidden: polls every 5 s while visible, not while hidden, and reads at once on return', async () => {
    vi.useFakeTimers();
    let visibility: DocumentVisibilityState = 'visible';
    vi.spyOn(document, 'visibilityState', 'get').mockImplementation(() => visibility);
    const stop = store().startPolling();
    await vi.advanceTimersByTimeAsync(5000);
    expect(status.getSystemStatus).toHaveBeenCalledTimes(1);
    visibility = 'hidden';
    await vi.advanceTimersByTimeAsync(15000);
    expect(status.getSystemStatus).toHaveBeenCalledTimes(1);
    visibility = 'visible';
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(0);
    expect(status.getSystemStatus).toHaveBeenCalledTimes(2);
    stop();
    await vi.advanceTimersByTimeAsync(20000);
    expect(status.getSystemStatus).toHaveBeenCalledTimes(2);
  });
});
