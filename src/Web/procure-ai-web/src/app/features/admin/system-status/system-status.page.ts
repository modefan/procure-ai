import { DatePipe } from '@angular/common';
import { Component, computed, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { Router, RouterLink, UrlTree } from '@angular/router';
import { BreakerDto } from '../../../api/notifications/models/breaker-dto';
import { ServiceHealth } from '../../../api/notifications/models/service-health';
import { AppConfigService } from '../../../core/config/app-config';
import { BusyButton } from '../../../core/ui/busy-button';
import { SystemStatusStore } from './system-status.store';

const TICK_MS = 1000;

/** Whole seconds from `now` until `at` (never negative). */
const secondsUntil = (at: string | null, now: number): number => (at ? Math.max(0, Math.ceil((Date.parse(at) - now) / 1000)) : 0);

/** "4:12" for 252 seconds. */
export const clock = (seconds: number): string => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;

const HEALTH_BADGE: Record<ServiceHealth, string> = {
  Healthy: 'badge-live',
  Degraded: 'badge-warn',
  Unhealthy: 'badge-error',
  Down: 'badge-error',
};

/** What each alert kind (a slug from the services) is called on screen. */
const ALERT_LABELS: Record<string, string> = {
  'finance-overdue': 'Finance step overdue',
  'outside-app-change': 'Changed outside the app',
};

/**
 * Screen 18: system status for reviewers (needs `admin.system`). Services with health and circuit breakers, the saga and outbox
 * numbers, alerts, the pgaudit summary, a link to the Aspire dashboard, and the supplier chaos switch. It reads every 5 s while
 * the tab is visible (see `SystemStatusStore.startPolling`); a one-second clock drives the countdowns.
 */
@Component({
  selector: 'app-system-status-page',
  imports: [BusyButton, DatePipe, RouterLink],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <div class="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 class="text-2xl font-bold tracking-tight text-slate-900">System status</h1>
          <p class="mt-1 text-slate-600">
            Refreshes every 5 seconds
            @if (store.status(); as status) {
              <span class="whitespace-nowrap">· updated {{ status.at | date: 'HH:mm:ss' }}</span>
            }
          </p>
        </div>
        <div class="flex flex-wrap gap-2">
          <a routerLink="/app/admin/conversations" class="btn-secondary whitespace-nowrap">Assistant conversations</a>
          @if (dashboardUrl()) {
            <a [href]="dashboardUrl()" target="_blank" rel="noopener noreferrer" class="btn-secondary whitespace-nowrap" data-testid="aspire">
              Open Aspire dashboard (traces, logs, metrics) <span aria-hidden="true">↗</span>
            </a>
          }
        </div>
      </div>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.load()">Try again</button>
        </div>
      }
      @if (store.refreshFailed()) {
        <p role="status" class="mt-4 rounded-lg border border-amber-200 bg-amber-50 px-4 py-2 text-sm text-amber-900">The last refresh failed. Showing the previous numbers.</p>
      }

      @if (store.isLoading() && !store.status()) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading the system status</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (store.status()) {
        <div class="mt-6 overflow-x-auto rounded-xl border border-slate-200 bg-white shadow-sm" data-testid="services">
          <!-- Below lg the table stacks into cards (display: block), which drops its table semantics; the explicit roles keep them. -->
          <table role="table" class="w-full text-left text-sm lg:min-w-[44rem] max-lg:block">
            <caption class="sr-only">Services with their health, instances and circuit breakers</caption>
            <thead role="rowgroup" class="border-b border-slate-200 bg-slate-50 text-xs font-semibold tracking-wide text-slate-600 uppercase max-lg:sr-only">
              <tr role="row">
                <th scope="col" role="columnheader" class="px-4 py-3">Service</th>
                <th scope="col" role="columnheader" class="px-4 py-3">Health</th>
                <th scope="col" role="columnheader" class="px-4 py-3 text-right">Instances</th>
                <th scope="col" role="columnheader" class="px-4 py-3">Circuit breakers</th>
              </tr>
            </thead>
            <tbody role="rowgroup" class="divide-y divide-slate-100 max-lg:block">
              @for (service of store.services(); track service.name) {
                <tr role="row" [attr.data-service]="service.name" class="max-lg:block max-lg:space-y-1.5 max-lg:px-4 max-lg:py-3">
                  <td role="cell" class="px-4 py-3 font-medium text-slate-900 max-lg:flex max-lg:items-center max-lg:justify-between max-lg:gap-3 max-lg:p-0">
                    {{ service.name }}
                    <span class="lg:hidden" [class]="badge(service.health)">{{ service.health }}</span>
                  </td>
                  <td role="cell" class="px-4 py-3 max-lg:hidden">
                    <span data-testid="health" [class]="badge(service.health)">{{ service.health }}</span>
                  </td>
                  <td role="cell" class="cell-label px-4 py-3 text-right tabular-nums text-slate-900 max-lg:block max-lg:p-0 max-lg:text-left" data-label="Instances">{{ service.instances }}</td>
                  <td role="cell" class="px-4 py-3 max-lg:block max-lg:p-0">
                    @for (breaker of service.breakers; track breaker.name) {
                      <div [class]="breakerClass(breaker)">{{ breakerText(breaker) }}</div>
                    } @empty {
                      <span class="text-slate-500">None</span>
                    }
                  </td>
                </tr>
              } @empty {
                <tr role="row">
                  <td role="cell" colspan="4" class="px-4 py-6 text-center text-slate-600">No services reported.</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <p class="mt-4 rounded-xl border border-slate-200 bg-white px-4 py-3 text-sm text-slate-800 shadow-sm" data-testid="stats">
          <span class="mr-5 inline-block whitespace-nowrap">Sagas running: <b>{{ store.counter('sagas_running') }}</b></span>
          <span class="mr-5 inline-block whitespace-nowrap">Timeouts due next hour: <b>{{ store.counter('timeouts_next_hour') }}</b></span>
          <span class="mr-5 inline-block whitespace-nowrap">Outbox backlog: <b>{{ store.outboxBacklog() }}</b></span>
          <span class="mr-5 inline-block whitespace-nowrap">Dead letters: <b>{{ store.deadLetters() }}</b> (broker {{ store.brokerDeadLetters() }})</span>
          <span class="mr-5 inline-block whitespace-nowrap">Duplicate requests coalesced today: <b>{{ store.counter('coalesced_today') }}</b></span>
          <span class="mr-5 inline-block whitespace-nowrap">Guard blocks today: <b>{{ store.counter('guard_blocks_today') }}</b></span>
          <span class="mr-5 inline-block whitespace-nowrap">Open finance alerts: <b>{{ store.counter('finance_alerts_open') }}</b></span>
          <span class="mr-5 inline-block whitespace-nowrap">Changed outside the app: <b>{{ store.counter('outside_app_alerts_open') }}</b></span>
        </p>

        <div class="mt-6 grid gap-6 lg:grid-cols-2">
          <section class="card" aria-labelledby="alerts-heading" data-testid="alerts">
            <h2 id="alerts-heading" class="text-lg font-semibold text-slate-900">Alerts</h2>
            @if (store.alerts().length > 0) {
              <ul class="mt-3 divide-y divide-slate-100 text-sm">
                @for (alert of store.alerts(); track $index) {
                  <li class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1 py-2">
                    <span class="min-w-0 break-words text-slate-800">
                      <span class="badge-warn mr-2">{{ kindLabel(alert.kind) }}</span>
                      @if (alert.link) {
                        <a [routerLink]="urlTree(alert.link)" class="text-indigo-700 hover:underline">{{ alert.text }}</a>
                      } @else {
                        {{ alert.text }}
                      }
                    </span>
                    <span class="text-xs whitespace-nowrap text-slate-500">{{ alert.at | date: 'd MMM HH:mm' }}</span>
                  </li>
                }
              </ul>
            } @else {
              <p class="mt-3 text-sm text-slate-600">No open alerts.</p>
            }
          </section>

          <section class="card" aria-labelledby="chaos-heading" data-testid="chaos">
            <h2 id="chaos-heading" class="text-lg font-semibold text-slate-900">Supplier API</h2>
            @if (broken()) {
              <p class="mt-3 text-sm text-slate-800">
                <span class="badge-error">{{ store.chaos()?.mode }}</span>
                @if (countdown(); as left) {
                  <span class="ml-2 whitespace-nowrap">restores in {{ left }}</span>
                }
              </p>
              <div class="mt-3">
                <app-busy-button variant="secondary" [busy]="store.chaosBusy()" busyLabel="Restoring…" (pressed)="store.setChaos('Normal')">Restore now</app-busy-button>
              </div>
            } @else {
              <p class="mt-3 text-sm text-slate-600">The simulated supplier is working normally. Break it to watch the circuit breaker open.</p>
              <div class="mt-3">
                <app-busy-button variant="secondary" [busy]="store.chaosBusy()" busyLabel="Breaking…" (pressed)="store.setChaos('Down')">Break the supplier API (chaos)</app-busy-button>
              </div>
            }
            @if (store.chaosError(); as message) {
              <p role="alert" class="mt-3 text-sm text-red-800">{{ message }}</p>
            }
          </section>
        </div>

        <section class="card mt-6" aria-labelledby="pgaudit-heading" data-testid="pgaudit">
          <h2 id="pgaudit-heading" class="text-lg font-semibold text-slate-900">Database access (pgaudit)</h2>
          @if (store.pgauditCounts().length > 0) {
            <p class="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-sm text-slate-800">
              @for (count of store.pgauditCounts(); track count.name) {
                <span class="whitespace-nowrap">{{ count.name }}: <b>{{ count.count }}</b></span>
              }
            </p>
          }
          @if (store.pgauditItems().length > 0) {
            <ul class="mt-3 space-y-1 font-mono text-xs text-slate-800">
              @for (item of store.pgauditItems(); track $index) {
                <li class="break-words"><span class="text-slate-500">{{ item.at | date: 'HH:mm:ss' }}</span> {{ item.text }}</li>
              }
            </ul>
          } @else if (store.pgauditCounts().length === 0) {
            <p class="mt-2 text-sm text-slate-600">No audited statements yet.</p>
          }
        </section>
      }
    </section>
  `,
})
export class SystemStatusPage implements OnInit {
  protected readonly store = inject(SystemStatusStore);
  private readonly router = inject(Router);
  private readonly appConfig = inject(AppConfigService);
  protected readonly dashboardUrl = computed(() => this.appConfig.config().observability.dashboardUrl);
  protected readonly skeletonRows = [1, 2, 3, 4];

  /** Wall clock, moved every second so countdowns tick between the 5-second reads. */
  private readonly now = signal(Date.now());
  protected readonly broken = computed(() => {
    const chaos = this.store.chaos();
    return chaos !== null && chaos.mode !== 'Normal';
  });
  protected readonly countdown = computed(() => {
    const at = this.store.chaos()?.restoresAt ?? null;
    return at ? clock(secondsUntil(at, this.now())) : null;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    const stopPolling = this.store.startPolling();
    const tick = setInterval(() => this.now.set(Date.now()), TICK_MS);
    destroyRef.onDestroy(() => {
      stopPolling();
      clearInterval(tick);
    });
  }

  ngOnInit(): void {
    void this.store.load();
  }

  protected kindLabel(kind: string): string {
    return ALERT_LABELS[kind] ?? kind;
  }

  protected badge(health: ServiceHealth): string {
    return HEALTH_BADGE[health];
  }

  protected breakerText(breaker: BreakerDto): string {
    switch (breaker.state) {
      case 'Open':
        return `${breaker.name}: OPEN (retry in ${secondsUntil(breaker.retryAt, this.now())} s)`;
      case 'HalfOpen':
        return `${breaker.name}: HALF-OPEN (testing)`;
      default:
        return `${breaker.name}: closed`;
    }
  }

  protected breakerClass(breaker: BreakerDto): string {
    return breaker.state === 'Open' ? 'font-semibold text-red-800' : breaker.state === 'HalfOpen' ? 'font-medium text-amber-800' : 'text-slate-600';
  }

  /** Alert links come from the server as app paths with a query string; routerLink needs them parsed. */
  protected urlTree(link: string): UrlTree {
    return this.router.parseUrl(link);
  }
}
