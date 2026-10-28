import { Component, effect, ElementRef, inject, Service, signal, viewChild } from '@angular/core';

export interface ConfirmOptions {
  title: string;
  body: string;
  /** Label of the button that goes ahead, e.g. "Cancel request". */
  confirm: string;
  /** Label of the button that backs out, e.g. "Keep it". */
  cancel: string;
}

/** "Are you sure?" for actions that matter. `ask()` resolves true on confirm, false on cancel or Esc. */
@Service()
export class ConfirmDialog {
  /** What is being asked; null while the dialog is closed. */
  readonly options = signal<ConfirmOptions | null>(null);
  private resolvePending: ((answer: boolean) => void) | null = null;

  ask(options: ConfirmOptions): Promise<boolean> {
    // A question already open is answered "no" before the new one replaces it.
    this.answer(false);
    this.options.set(options);
    return new Promise<boolean>((resolve) => (this.resolvePending = resolve));
  }

  answer(confirmed: boolean): void {
    const resolve = this.resolvePending;
    this.resolvePending = null;
    this.options.set(null);
    resolve?.(confirmed);
  }
}

/** Put one `<app-confirm-dialog />` in the app root. A native modal dialog: the browser gives the focus trap and Esc. */
@Component({
  selector: 'app-confirm-dialog',
  template: `
    <dialog
      #dialog
      class="m-auto w-[calc(100%-2rem)] max-w-md rounded-xl border border-slate-200 bg-white p-6 shadow-xl backdrop:bg-slate-900/50"
      aria-labelledby="confirm-title"
      aria-describedby="confirm-text"
      (close)="confirm.answer(false)"
    >
      @if (confirm.options(); as o) {
        <h2 id="confirm-title" class="text-lg font-semibold text-slate-900">{{ o.title }}</h2>
        <p id="confirm-text" class="mt-2 text-sm text-slate-600">{{ o.body }}</p>
        <div class="mt-6 flex flex-wrap justify-end gap-3">
          <button type="button" class="btn-secondary" (click)="confirm.answer(false)">{{ o.cancel }}</button>
          <button type="button" class="btn-primary" (click)="confirm.answer(true)">{{ o.confirm }}</button>
        </div>
      }
    </dialog>
  `,
})
export class ConfirmDialogHost {
  protected readonly confirm = inject(ConfirmDialog);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  constructor() {
    effect(() => {
      const element = this.dialog().nativeElement;
      const open = this.confirm.options() !== null;
      if (open && !element.open) {
        element.showModal();
      } else if (!open && element.open) {
        element.close();
      }
    });
  }
}
