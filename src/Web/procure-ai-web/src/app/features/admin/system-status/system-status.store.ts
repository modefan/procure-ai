import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { StatusItem } from '../../../api/notifications/models/status-item';
import { SystemStatusDto } from '../../../api/notifications/models/system-status-dto';
import { SystemStatusService } from '../../../api/notifications/services/system-status.service';
import { ChaosDto } from '../../../api/purchasing/models/chaos-dto';
import { ChaosMode } from '../../../api/purchasing/models/chaos-mode';
import { SupplierChaosService } from '../../../api/purchasing/services/supplier-chaos.service';
import { idempotencyKey } from '../../../core/http/idempotency';
import { Logger } from '../../../core/logging/logger';
import { withCache } from '../../../core/state/with-cache';
import { withRequestStatus } from '../../../core/state/with-request-status';

/** How often the page reads the status while it is visible (plan ruling 9). */
export const STATUS_REFRESH_MS = 5000;

/** The counters the page shows. The server leaves out zero counters, so each one is 0 until it says otherwise. */
export const KNOWN_COUNTERS = [
  'sagas_running',
  'timeouts_next_hour',
  'finance_alerts_open',
  'outside_app_alerts_open',
  'guard_blocks_today',
  'questions_today',
  'coalesced_today',
] as const;

const PGAUDIT_PREFIX = 'pgaudit_';
const PGAUDIT_KIND = 'pgaudit';
const CHAOS_FAILED = 'The supplier API setting could not be changed. Please try again.';

interface SystemStatusState {
  status: SystemStatusDto | null;
  /** The last background read failed; the page keeps showing the previous answer. */
  refreshFailed: boolean;
  chaosBusy: boolean;
  chaosError: string | null;
}

/**
 * Screen 18, system status (`admin.system`). Read-only except the supplier chaos switch. Polls while the tab is visible and
 * pauses while it is hidden (`startPolling`), because the status endpoint fans out to every service.
 */
export const SystemStatusStore = signalStore(
  { providedIn: 'root' },
  withState<SystemStatusState>({ status: null, refreshFailed: false, chaosBusy: false, chaosError: null }),
  withRequestStatus(),
  withCache(),
  withComputed((store) => ({
    services: computed(() => store.status()?.services ?? []),
    chaos: computed<ChaosDto | null>(() => store.status()?.supplierChaos ?? null),
    /** Every known counter (0 when absent) plus whatever else the server sent. */
    counters: computed<Record<string, number>>(() => ({
      ...Object.fromEntries(KNOWN_COUNTERS.map((name) => [name, 0])),
      ...(store.status()?.counters ?? {}),
    })),
    /** Alerts: everything that is not the pgaudit list. */
    alerts: computed<StatusItem[]>(() => (store.status()?.alerts ?? []).filter((a) => a.kind !== PGAUDIT_KIND)),
    pgauditItems: computed<StatusItem[]>(() => (store.status()?.alerts ?? []).filter((a) => a.kind === PGAUDIT_KIND)),
    pgauditCounts: computed(() =>
      Object.entries(store.status()?.counters ?? {})
        .filter(([name]) => name.startsWith(PGAUDIT_PREFIX))
        .map(([name, count]) => ({ name: name.slice(PGAUDIT_PREFIX.length), count })),
    ),
    /** Messages waiting to leave the services (outbox), summed over the services. */
    outboxBacklog: computed(() => (store.status()?.services ?? []).reduce((sum, s) => sum + s.messages.outgoing, 0)),
    /** Failed messages kept in the services' tables, summed. */
    deadLetters: computed(() => (store.status()?.services ?? []).reduce((sum, s) => sum + s.messages.deadLetters, 0)),
    /** One broker queue shared by all services: the largest value reported, not a sum. */
    brokerDeadLetters: computed(() => Math.max(0, ...(store.status()?.services ?? []).map((s) => s.messages.brokerDeadLetters))),
  })),
  withMethods((store) => {
    const api = inject(SystemStatusService);
    const purchasing = inject(SupplierChaosService);
    const log = inject(Logger).withSource('SystemStatusStore');
    let latest = 0;

    const read = async (): Promise<void> => {
      const call = ++latest;
      const epoch = store.cacheEpoch();
      if (store.status() === null) {
        store.setLoading();
      }
      try {
        const status = await api.getSystemStatus();
        if (call !== latest || epoch !== store.cacheEpoch()) {
          return;
        }
        patchState(store, { status, refreshFailed: false });
        store.setLoaded();
      } catch (error) {
        if (call !== latest || epoch !== store.cacheEpoch()) {
          return;
        }
        log.warn('Reading the system status failed', error);
        if (store.status() === null) {
          store.setError(error, 'The system status could not be loaded.');
        } else {
          patchState(store, { refreshFailed: true });
        }
      }
    };

    return {
      counter: (name: string): number => store.counters()[name] ?? 0,

      load: read,

      /** Reads every `STATUS_REFRESH_MS` while the tab is visible, and at once when it becomes visible again. Returns the stop function. */
      startPolling(): () => void {
        const visible = () => document.visibilityState === 'visible';
        const timer = setInterval(() => {
          if (visible()) {
            void read();
          }
        }, STATUS_REFRESH_MS);
        const onVisibility = () => {
          if (visible()) {
            void read();
          }
        };
        document.addEventListener('visibilitychange', onVisibility);
        return () => {
          clearInterval(timer);
          document.removeEventListener('visibilitychange', onVisibility);
        };
      },

      /** Sets the simulated supplier API's mode (Failing to break it, Normal to restore it). One idempotency key per call. */
      async setChaos(mode: ChaosMode): Promise<void> {
        if (store.chaosBusy()) {
          return;
        }
        const epoch = store.cacheEpoch();
        patchState(store, { chaosBusy: true, chaosError: null });
        try {
          const chaos = await purchasing.setSupplierChaos({ body: { mode }, 'Idempotency-Key': idempotencyKey() });
          if (epoch !== store.cacheEpoch()) {
            return;
          }
          const status = store.status();
          if (status) {
            patchState(store, { status: { ...status, supplierChaos: chaos } });
          }
          // The next poll (within 5 s) brings the server's own view; the answer above is shown meanwhile.
        } catch (error) {
          log.warn('Setting the supplier chaos mode failed', error);
          if (epoch === store.cacheEpoch()) {
            patchState(store, { chaosError: CHAOS_FAILED });
          }
        } finally {
          if (epoch === store.cacheEpoch()) {
            patchState(store, { chaosBusy: false });
          }
        }
      },
    };
  }),
);

export type SystemStatusStore = InstanceType<typeof SystemStatusStore>;
