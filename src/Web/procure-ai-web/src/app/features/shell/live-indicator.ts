import { Component, computed, inject } from '@angular/core';
import { LiveConnection } from '../../core/live/live-connection';

/** The dot and word in the header: Live (green), Reconnecting… (orange), Offline (grey). */
@Component({
  selector: 'app-live-indicator',
  template: `
    <span
      data-testid="live-indicator"
      role="status"
      [attr.title]="label()"
      class="inline-flex items-center gap-1.5 whitespace-nowrap rounded-full px-2 py-1 text-xs font-medium text-slate-600"
    >
      <span aria-hidden="true" class="size-2 rounded-full" [class]="dotClass()"></span>
      <span class="max-sm:sr-only">{{ label() }}</span>
    </span>
  `,
})
export class LiveIndicator {
  private readonly live = inject(LiveConnection);

  protected readonly label = computed(() => {
    switch (this.live.state()) {
      case 'connected':
        return 'Live';
      case 'reconnecting':
        return 'Reconnecting…';
      case 'connecting':
        return 'Connecting…';
      default:
        return 'Offline';
    }
  });

  protected readonly dotClass = computed(() => {
    switch (this.live.state()) {
      case 'connected':
        return 'bg-emerald-500';
      case 'reconnecting':
      case 'connecting':
        return 'bg-amber-500 animate-pulse motion-reduce:animate-none';
      default:
        return 'bg-slate-400';
    }
  });
}
