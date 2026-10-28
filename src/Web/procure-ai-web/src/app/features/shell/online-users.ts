import { Component, computed, inject } from '@angular/core';
import { PresenceStore } from '../../core/live/presence.store';

const SHOWN = 3;

/** Initials of the other people online, as small circles (first three, then "+n"). Nothing when I am alone. */
@Component({
  selector: 'app-online-users',
  template: `
    @if (people().length > 0) {
      <span
        data-testid="online-users"
        role="img"
        class="inline-flex items-center"
        [attr.aria-label]="label()"
        [attr.title]="label()"
      >
        @for (person of shown(); track person.userId) {
          <span
            aria-hidden="true"
            class="-ml-1.5 inline-flex size-7 items-center justify-center rounded-full border-2 border-white bg-indigo-100 text-[11px] font-semibold text-indigo-800 first:ml-0"
          >{{ initials(person.name) }}</span>
        }
        @if (extra() > 0) {
          <span
            aria-hidden="true"
            class="-ml-1.5 inline-flex size-7 items-center justify-center rounded-full border-2 border-white bg-slate-200 text-[11px] font-semibold whitespace-nowrap text-slate-700"
          >+{{ extra() }}</span>
        }
      </span>
    }
  `,
})
export class OnlineUsers {
  private readonly presence = inject(PresenceStore);

  protected readonly people = this.presence.online;
  protected readonly shown = computed(() => this.people().slice(0, SHOWN));
  protected readonly extra = computed(() => Math.max(0, this.people().length - SHOWN));
  protected readonly label = computed(() => `Online now: ${this.people().map((p) => p.name).join(', ')}`);

  protected initials(name: string): string {
    const parts = name.trim().split(/\s+/).filter(Boolean);
    return ((parts[0]?.[0] ?? '?') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase();
  }
}
