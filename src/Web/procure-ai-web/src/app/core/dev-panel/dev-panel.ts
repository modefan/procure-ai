import { Component, computed, inject, signal } from '@angular/core';
import { AppConfigService } from '../config/app-config';
import { DevCall, DevPanelStore } from './dev-panel.store';

/** `${dashboardUrl}/traces/detail/${traceId}`, or null when no dashboard is configured. */
export function traceLink(dashboardUrl: string, traceId: string): string | null {
  const base = dashboardUrl.trim().replace(/\/+$/, '');
  return base ? `${base}/traces/detail/${encodeURIComponent(traceId)}` : null;
}

/** `4bf92f35…736`: enough to recognise a trace id at a glance. */
export function shortTrace(traceId: string): string {
  return traceId.length > 14 ? `${traceId.slice(0, 8)}…${traceId.slice(-3)}` : traceId;
}

/**
 * "Behind the scenes" (wireframe 19): a bottom sheet with the last API calls and what the server did with each (cache, coalescing,
 * idempotent replay, policy, tenant, instance, ETag, trace). Shown by the shell while the panel is open.
 */
@Component({
  selector: 'app-dev-panel',
  template: `
    <section
      data-testid="dev-panel"
      aria-label="Behind the scenes"
      class="fixed inset-x-0 bottom-0 z-30 flex max-h-[40vh] flex-col border-t-2 border-indigo-600 bg-slate-900 text-slate-100 shadow-[0_-4px_12px_rgb(0_0_0/0.25)]"
    >
      <header class="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2">
        <h2 class="text-sm font-semibold">Behind the scenes</h2>
        @if (!store.collapsed()) {
          <p class="hidden min-w-0 flex-1 truncate text-xs text-slate-300 md:block">Makes the hidden patterns visible during a two-minute review.</p>
        } @else {
          <span class="min-w-0 flex-1 text-xs text-slate-300">{{ store.calls().length }} calls</span>
        }
        @if (!store.collapsed()) {
          <label class="flex items-center gap-1 text-xs text-slate-300">
            <span class="sr-only md:not-sr-only">Method</span>
            <select
              data-testid="dev-method-filter"
              class="rounded border border-slate-600 bg-slate-800 px-1 py-0.5 text-xs text-slate-100"
              aria-label="Filter by method"
              (change)="method.set($any($event.target).value)"
            >
              <option value="">All</option>
              @for (m of methods(); track m) {
                <option [value]="m">{{ m }}</option>
              }
            </select>
          </label>
          <button type="button" data-testid="dev-clear" class="dev-btn" (click)="store.clear()">Clear</button>
        }
        <button type="button" data-testid="dev-collapse" class="dev-btn" [attr.aria-expanded]="!store.collapsed()" (click)="store.collapsed.set(!store.collapsed())">
          {{ store.collapsed() ? 'Expand' : 'Collapse' }}
        </button>
        <button type="button" data-testid="dev-close" class="dev-btn" aria-label="Close behind the scenes" (click)="store.setOpen(false)">&#10005;</button>
      </header>

      @if (!store.collapsed()) {
        @if (visible().length === 0) {
          <p class="px-3 pb-3 text-sm text-slate-300">No calls yet. Use the app and they appear here.</p>
        } @else {
          <ul class="min-h-0 flex-1 divide-y divide-slate-700 overflow-y-auto border-t border-slate-700 font-mono text-xs">
            @for (call of visible(); track call.at + call.url + $index) {
              <li data-testid="dev-call" class="px-3 py-1.5">
                <div data-testid="dev-call-summary" class="flex flex-wrap items-baseline gap-x-2">
                  <span class="min-w-0 break-all"><strong class="font-semibold">{{ call.method }}</strong> {{ call.url }}</span>
                  <span [class]="call.status >= 400 || call.status === 0 ? 'text-rose-300' : 'text-emerald-300'">{{ call.status }}</span>
                  <span class="whitespace-nowrap">{{ call.ms }} ms</span>
                  @if (call.cache; as cache) {
                    <span class="whitespace-nowrap" [class]="cache.kind === 'hit' ? 'text-sky-300' : 'text-slate-300'">cache {{ cache.kind.toUpperCase() }}{{ age(cache.ageMs) }}</span>
                  }
                  @if (call.coalesced) {
                    <span class="text-amber-300">joined an identical request</span>
                  }
                  @if (call.replayed) {
                    <span class="text-amber-300">idempotent replay</span>
                  }
                </div>
                <div data-testid="dev-call-detail" class="flex flex-wrap items-baseline gap-x-3 text-slate-300">
                  @if (call.traceId; as id) {
                    @if (link(id); as href) {
                      <span>trace <a data-testid="trace-link" [href]="href" target="_blank" rel="noopener" class="text-indigo-300 underline">{{ short(id) }} open in Aspire &#8599;</a></span>
                    } @else {
                      <span>trace {{ short(id) }}</span>
                    }
                  }
                  @if (call.etag) {
                    <span>ETag {{ call.etag }}</span>
                  }
                  @if (call.tenant) {
                    <span>tenant {{ call.tenant }}</span>
                  }
                  @if (call.policy) {
                    <span>policy {{ call.policy }} {{ call.status === 403 ? '✗' : '✓' }}</span>
                  }
                  @if (call.instance) {
                    <span>{{ call.instance }}</span>
                  }
                </div>
              </li>
            }
          </ul>
        }
      }
    </section>
  `,
})
export class DevPanel {
  protected readonly store = inject(DevPanelStore);
  private readonly config = inject(AppConfigService);
  protected readonly method = signal('');
  protected readonly methods = computed(() => [...new Set(this.store.calls().map((c) => c.method))].sort());
  protected readonly visible = computed<DevCall[]>(() => this.store.calls().filter((c) => !this.method() || c.method === this.method()));

  protected link(traceId: string): string | null {
    return traceLink(this.config.config().observability.dashboardUrl, traceId);
  }

  protected short = shortTrace;

  protected age(ageMs: number | undefined): string {
    return ageMs === undefined ? '' : ` (${(ageMs / 1000).toFixed(1)} s old)`;
  }
}
