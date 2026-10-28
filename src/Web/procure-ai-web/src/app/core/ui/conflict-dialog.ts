import { Component, effect, ElementRef, inject, Service, signal, viewChild } from '@angular/core';

/**
 * "Someone else changed this" (HTTP 412). A store calls `show(message)`, then reloads the latest version; the promise
 * resolves when the user has read it. A second call while the dialog is open shares the open dialog.
 */
@Service()
export class ConflictDialog {
  /** The message being shown; null while the dialog is closed. */
  readonly message = signal<string | null>(null);
  private pending: Promise<void> | null = null;
  private resolvePending: (() => void) | null = null;

  show(message: string): Promise<void> {
    if (this.pending) {
      return this.pending;
    }
    this.message.set(message);
    this.pending = new Promise<void>((resolve) => (this.resolvePending = resolve));
    return this.pending;
  }

  dismiss(): void {
    if (!this.pending) {
      return;
    }
    const resolve = this.resolvePending;
    this.pending = null;
    this.resolvePending = null;
    this.message.set(null);
    resolve?.();
  }
}

/** Put one `<app-conflict-dialog />` in the app root. A native modal dialog: the browser gives the focus trap and Esc. */
@Component({
  selector: 'app-conflict-dialog',
  template: `
    <dialog
      #dialog
      class="m-auto w-[calc(100%-2rem)] max-w-md rounded-xl border border-slate-200 bg-white p-6 shadow-xl backdrop:bg-slate-900/50"
      aria-labelledby="conflict-title"
      aria-describedby="conflict-text"
      (close)="conflict.dismiss()"
    >
      <h2 id="conflict-title" class="text-lg font-semibold text-slate-900">This was changed by someone else</h2>
      <p id="conflict-text" class="mt-2 text-sm text-slate-600">{{ conflict.message() }}</p>
      <p class="mt-2 text-sm text-slate-600">The latest version is loaded. Check it, then try again if you still want to.</p>
      <div class="mt-6 flex justify-end">
        <button type="button" class="btn-primary" (click)="conflict.dismiss()">Show the latest</button>
      </div>
    </dialog>
  `,
})
export class ConflictDialogHost {
  protected readonly conflict = inject(ConflictDialog);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  constructor() {
    effect(() => {
      const element = this.dialog().nativeElement;
      const open = this.conflict.message() !== null;
      if (open && !element.open) {
        element.showModal();
      } else if (!open && element.open) {
        element.close();
      }
    });
  }
}
