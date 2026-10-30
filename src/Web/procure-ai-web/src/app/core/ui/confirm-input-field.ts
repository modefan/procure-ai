import { afterNextRender, Component, effect, ElementRef, input, signal, viewChild, WritableSignal } from '@angular/core';
import { form, FormField, maxLength } from '@angular/forms/signals';
import type { ConfirmInput } from './confirm-dialog';

/**
 * The text box of `ConfirmDialog.askWithInput`. It is its own component so the dialog host (which sits in the app root)
 * can import it on demand: Signal Forms then stay out of the initial bundle. The host owns the text (`sink`) and the validation.
 */
@Component({
  selector: 'app-confirm-input-field',
  imports: [FormField],
  template: `
    <label for="confirm-input" class="field-label">{{ config().label }}</label>
    <textarea
      #area
      id="confirm-input"
      rows="3"
      class="field-input"
      [attr.aria-invalid]="error() ? 'true' : null"
      [attr.aria-describedby]="error() ? 'confirm-input-error' : null"
      [formField]="fieldForm.text"
    ></textarea>
    <div class="mt-1 flex items-start justify-between gap-3">
      <div class="min-w-0">
        @if (error(); as message) {
          <p id="confirm-input-error" class="field-error" role="alert">{{ message }}</p>
        }
      </div>
      <span class="shrink-0 text-xs whitespace-nowrap tabular-nums text-slate-500">{{ fieldModel().text.length }} / {{ config().maxLength }}</span>
    </div>
  `,
})
export class ConfirmInputField {
  readonly config = input.required<ConfirmInput>();
  /** Where the typed text (not trimmed) is written, for the host to read. */
  readonly sink = input.required<WritableSignal<string>>();
  /** The complaint to show, or null. */
  readonly error = input<string | null>(null);

  private readonly area = viewChild.required<ElementRef<HTMLTextAreaElement>>('area');
  private readonly fieldModel = signal({ text: '' });
  protected readonly fieldForm = form(this.fieldModel, (p) => {
    maxLength(p.text, () => this.config().maxLength);
  });

  constructor() {
    effect(() => this.sink().set(this.fieldModel().text));
    afterNextRender(() => this.area().nativeElement.focus());
  }
}
