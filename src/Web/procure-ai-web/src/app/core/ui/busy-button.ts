import { Component, computed, input, output } from '@angular/core';

/**
 * The button for every write: while `busy` it is disabled, shows a spinner and `busyLabel`, and ignores clicks.
 * Writes wait for the server (no optimistic updates), so this is what the user sees in between.
 * `<app-busy-button [busy]="store.isBusy()" busyLabel="Approving…" (pressed)="approve()">Approve</app-busy-button>`
 */
@Component({
  selector: 'app-busy-button',
  template: `
    <button
      [type]="type()"
      [class]="classes()"
      [disabled]="busy() || disabled()"
      [attr.aria-busy]="busy() ? 'true' : null"
      (click)="onClick($event)"
    >
      @if (busy()) {
        <span class="size-4 animate-spin rounded-full border-2 border-current border-t-transparent" aria-hidden="true"></span>
        <span>{{ busyLabel() || 'Working…' }}</span>
      } @else {
        <ng-content />
      }
    </button>
  `,
})
export class BusyButton {
  readonly busy = input(false);
  readonly disabled = input(false);
  readonly busyLabel = input('');
  readonly variant = input<'primary' | 'secondary' | 'ghost'>('primary');
  readonly type = input<'button' | 'submit'>('button');
  /** Emitted for a click while the button is neither busy nor disabled. */
  readonly pressed = output<void>();

  protected readonly classes = computed(() => `btn-${this.variant()} disabled:cursor-not-allowed disabled:opacity-60`);

  protected onClick(event: Event): void {
    if (this.busy() || this.disabled()) {
      event.preventDefault();
      return;
    }
    this.pressed.emit();
  }
}
