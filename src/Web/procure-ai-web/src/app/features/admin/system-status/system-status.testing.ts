import { SystemStatusDto } from '../../../api/notifications/models/system-status-dto';
import { ServiceStatusDto } from '../../../api/notifications/models/service-status-dto';

export const serviceStatus = (name: string, o: Partial<ServiceStatusDto> = {}): ServiceStatusDto => ({
  name,
  health: 'Healthy',
  instances: 1,
  breakers: [],
  messages: { incoming: 0, outgoing: 0, scheduled: 0, deadLetters: 0, brokerDeadLetters: 0 },
  ...o,
});

/** A healthy system as the Notifications service reports it (zero counters are omitted by the server). */
export const statusDto = (o: Partial<SystemStatusDto> = {}): SystemStatusDto => ({
  at: '2026-10-30T10:00:00Z',
  services: [serviceStatus('Requests'), serviceStatus('Purchasing', { instances: 2 }), serviceStatus('Approvals')],
  counters: { sagas_running: 14, timeouts_next_hour: 2, coalesced_today: 37, guard_blocks_today: 3 },
  alerts: [],
  supplierChaos: null,
  ...o,
});
