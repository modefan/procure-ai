import { NgComponentOutlet } from '@angular/common';
import { Component, computed, effect, ElementRef, inject, Service, signal, Type, viewChild } from '@angular/core';

export interface ConfirmOptions {
  title: string;
  body: string;
  /** Label of the button that goes ahead, e.g. "Cancel request". */
  confirm: string;
  /** Label of the button that backs out, e.g. "Keep it". */
  cancel: string;
}

/** A text box in the dialog, e.g. the reason for closing an order short. */
export interface ConfirmInput {
  label: string;
  /** Shortest accepted text once trimmed (0 = no minimum). Applies to an optional input only when something is typed. */
  minLength: number;
  maxLength: number;
  /** True: the text must be at least `minLength` long (and not blank). False: it may stay empty. */
  required: boolean;
}

export interface InputConfirmOptions extends ConfirmOptions {
  input: ConfirmInput;
}

export interface InputAnswer {
  confirmed: boolean;
  /** The trimmed text; empty when the user backed out. */
  value: string;
}

/**
 * "Are you sure?" for actions that matter. `ask()` resolves true on confirm, false on cancel or Esc.
 * `askWithInput()` adds a text box and resolves `{ confirmed, value }`; confirming is refused until the text is valid.
 */
@Service()
export class ConfirmDialog {
  /** What is being asked; null while the dialog is closed. */
  readonly options = signal<(ConfirmOptions & { input?: ConfirmInput }) | null>(null);
  private resolvePending: ((answer: InputAnswer) => void) | null = null;

  ask(options: ConfirmOptions): Promise<boolean> {
    return this.open(options).then((answer) => answer.confirmed);
  }

  askWithInput(options: InputConfirmOptions): Promise<InputAnswer> {
    return this.open(options);
  }

  answer(confirmed: boolean, value = ''): void {
    const resolve = this.resolvePending;
    this.resolvePending = null;
    this.options.set(null);
    resolve?.({ confirmed, value: confirmed ? value : '' });
  }

  private open(options: ConfirmOptions & { input?: ConfirmInput }): Promise<InputAnswer> {
    // A question already open is answered "no" before the new one replaces it.
    this.answer(false);
    this.options.set(options);
    return new Promise<InputAnswer>((resolve) => (this.resolvePending = resolve));
  }
}

/** Put one `<app-confirm-dialog />` in the app root. A native modal dialog: the browser gives the focus trap and Esc. */
@Component({
  selector: 'app-confirm-dialog',
  imports: [NgComponentOutlet],
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
        @if (o.input; as input) {
          <div class="mt-4">
            @if (inputField(); as field) {
              <ng-container *ngComponentOutlet="field; inputs: { config: input, sink: text, error: showError() ? error() : null }" />
            }
          </div>
        }
        <div class="mt-6 flex flex-wrap justify-end gap-3">
          <button type="button" class="btn-secondary" (click)="confirm.answer(false)">{{ o.cancel }}</button>
          <button type="button" class="btn-primary" (click)="accept()">{{ o.confirm }}</button>
        </div>
      }
    </dialog>
  `,
})
export class ConfirmDialogHost {
  protected readonly confirm = inject(ConfirmDialog);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  /** The text box component, imported the first time a question has an input (it brings Signal Forms; the root stays small). */
  protected readonly inputField = signal<Type<unknown> | null>(null);
  /** What is typed in the box (the box itself is loaded on demand, see `ConfirmInputField`). */
  protected readonly text = signal('');
  /** True once the user pressed the confirm button on an invalid text. */
  private readonly tried = signal(false);

  /** What is wrong with the text, or null. */
  protected readonly error = computed<string | null>(() => {
    const input = this.confirm.options()?.input;
    if (!input) {
      return null;
    }
    const length = this.text().trim().length;
    if (length === 0 && !input.required) {
      return null;
    }
    return length < Math.max(input.minLength, 1) ? (input.minLength > 1 ? `Enter at least ${input.minLength} characters.` : 'Enter some text.') : null;
  });

  protected readonly showError = computed(() => this.tried() && this.error() !== null);

  constructor() {
    effect(() => {
      if (this.confirm.options()?.input && !this.inputField()) {
        void import('./confirm-input-field').then((m) => this.inputField.set(m.ConfirmInputField));
      }
    });
    effect(() => {
      const element = this.dialog().nativeElement;
      const open = this.confirm.options() !== null;
      if (open && !element.open) {
        // Every question starts with an empty box and no complaint.
        this.text.set('');
        this.tried.set(false);
        element.showModal();
      } else if (!open && element.open) {
        element.close();
      }
    });
  }

  protected accept(): void {
    if (this.confirm.options()?.input) {
      this.tried.set(true);
      if (this.error() !== null) {
        return;
      }
    }
    this.confirm.answer(true, this.text().trim());
  }
}
