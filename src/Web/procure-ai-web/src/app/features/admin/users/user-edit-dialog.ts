import { Component, computed, effect, ElementRef, inject, input, OnDestroy, output, signal, viewChild } from '@angular/core';
import { Listbox, Option } from '@angular/aria/listbox';
import { form, FormField, submit, validate } from '@angular/forms/signals';
import { CostCenterAdminDto } from '../../../api/access/models/cost-center-admin-dto';
import { UserDto } from '../../../api/access/models/user-dto';
import { BusyButton } from '../../../core/ui/busy-button';
import { formatRand } from '../../../core/ui/money.pipe';
import { UsersStore } from './users.store';

/** The largest approval limit the Access service accepts. */
export const MAX_LIMIT = 1_000_000_000;

/**
 * Reads an amount as typed in en-ZA: `200 000,00`, `200000`, `200000.50`. Spaces (also no-break ones) are grouping, a comma or a
 * dot is the decimal mark, at most two decimals. Returns null for anything else (text, a minus sign, two decimal marks).
 */
export function parseRandInput(raw: string): number | null {
  const text = raw.replace(/[\s  ]/g, '').replace(/^R/i, '').replace(',', '.');
  if (!/^\d+(\.\d{1,2})?$/.test(text)) {
    return null;
  }
  return Number(text);
}

/**
 * Edit one user's cost centers (Angular Aria listbox, several can be chosen) and approval limit. A native modal dialog like the
 * budget dialog: the browser traps the focus and Esc closes it (not while a save runs). Saving sends only what changed (see
 * `UsersStore.save`). The server's refusals show next to the field they belong to. A conflict (someone else saved first) keeps the
 * dialog open with this admin's own changes on top of the latest values, like the matrix: the cost centers they ticked or unticked are
 * applied to the latest list, a limit they changed stays, and whatever they did not touch takes the latest value (so Save never writes
 * back an old value over someone else's change). `user` is the live row of the store, so Save goes out on the latest version.
 */
@Component({
  selector: 'app-user-edit-dialog',
  imports: [FormField, BusyButton, Listbox, Option],
  template: `
    <dialog
      #dialog
      class="m-auto w-[calc(100%-2rem)] max-w-lg rounded-xl border border-slate-200 bg-white p-6 shadow-xl backdrop:bg-slate-900/50"
      aria-labelledby="user-edit-title"
      (cancel)="onCancel($event)"
      (close)="closed.emit()"
    >
      <h2 id="user-edit-title" class="text-lg font-semibold text-slate-900">Edit {{ user().displayName }}</h2>
      <p class="mt-1 text-sm break-all text-slate-600">{{ user().email }}</p>

      <form novalidate class="mt-4" (submit)="$event.preventDefault()">
        <span id="user-codes-label" class="field-label">Cost centers</span>
        <ul
          ngListbox
          [multi]="true"
          selectionMode="explicit"
          [(value)]="codes"
          [disabled]="store.saving()"
          aria-labelledby="user-codes-label"
          [attr.aria-invalid]="store.codesError() ? 'true' : null"
          [attr.aria-describedby]="store.codesError() ? 'user-codes-error' : 'user-codes-hint'"
          class="scroll-hint max-h-52 overflow-y-auto rounded-lg border border-slate-300 bg-white p-1 text-sm shadow-sm focus-within:outline-2 focus-within:outline-indigo-700 aria-[invalid=true]:border-red-600"
        >
          @for (center of costCenters(); track center.code) {
            <li ngOption [value]="center.code" [label]="center.code + ' ' + center.name" [attr.data-code]="center.code" class="cost-option">
              <span class="cost-check" aria-hidden="true">&#10003;</span>
              <span class="font-mono text-[0.8125rem] font-semibold text-slate-900">{{ center.code }}</span>
              <span class="min-w-0 truncate text-slate-700">{{ center.name }}</span>
            </li>
          }
        </ul>
        @if (store.codesError(); as message) {
          <p id="user-codes-error" role="alert" class="field-error">{{ message }}</p>
        } @else {
          <p id="user-codes-hint" class="mt-1 text-sm text-slate-600">
            Press Space or click to tick a cost center. {{ codes().length }} of {{ costCenters().length }} cost centers chosen.
          </p>
        }

        @if (refreshed(); as latest) {
          <p class="mt-2 text-sm text-amber-800" data-hint="refreshed" role="status">
            {{ user().displayName }} was just changed by someone else. Now: {{ latest }}. Your changes are kept on top; Save applies them.
          </p>
        }

        <label for="user-limit" class="field-label mt-4">Approval limit (Rand)</label>
        <input
          id="user-limit"
          type="text"
          inputmode="decimal"
          autocomplete="off"
          class="field-input text-right tabular-nums"
          [formField]="limitForm.limit"
          [attr.aria-invalid]="limitError() ? 'true' : null"
          [attr.aria-describedby]="limitError() ? 'user-limit-error' : 'user-limit-hint'"
        />
        @if (limitError(); as message) {
          <p id="user-limit-error" role="alert" class="field-error">{{ message }}</p>
        } @else {
          <p id="user-limit-hint" class="mt-1 text-sm text-slate-600">From R 0,00 to {{ max }}. Type 200 000,00 or 200000.</p>
        }

        <div class="mt-6 flex flex-wrap justify-end gap-3">
          <button type="button" class="btn-secondary" [disabled]="store.saving()" (click)="close()">Cancel</button>
          <app-busy-button type="submit" [busy]="store.saving()" busyLabel="Saving…" (pressed)="save()">Save</app-busy-button>
        </div>
      </form>
    </dialog>
  `,
})
export class UserEditDialog implements OnDestroy {
  protected readonly store = inject(UsersStore);
  readonly user = input.required<UserDto>();
  readonly costCenters = input.required<CostCenterAdminDto[]>();
  /** The dialog is closed (saved, cancelled, conflict or Esc); the parent removes it. */
  readonly closed = output<void>();

  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  protected readonly codes = signal<string[]>([]);
  private readonly model = signal<{ limit: string }>({ limit: '' });
  private readonly tried = signal(false);
  protected readonly max = formatRand(MAX_LIMIT);
  /** After a conflict: the latest saved values, in words; the typed ones stay in the form. */
  protected readonly refreshed = computed(() => {
    if (!this.conflicted()) {
      return null;
    }
    const user = this.user();
    const codes = user.costCenterCodes.length > 0 ? user.costCenterCodes.join(', ') : 'no cost centers';
    return `${codes}, limit ${formatRand(user.approvalLimit)}`;
  });
  private readonly conflicted = signal(false);
  /** What the form started from: the values when the dialog opened, then the latest after each conflict. */
  private base: { codes: string[]; limit: number } = { codes: [], limit: 0 };

  protected readonly limitForm = form(this.model, (p) => {
    validate(p.limit, (ctx) => {
      const amount = parseRandInput(ctx.value());
      if (amount === null) {
        return { kind: 'number', message: 'Enter an amount of 0 or more, e.g. 200 000,00.' };
      }
      return amount > MAX_LIMIT ? { kind: 'max', message: `The limit cannot be above ${formatRand(MAX_LIMIT)}.` } : undefined;
    });
  });

  protected readonly limitError = computed(() => {
    const state = this.limitForm.limit();
    if ((state.touched() || this.tried()) && state.errors().length > 0) {
      return state.errors()[0].message ?? 'Check this value';
    }
    return this.store.limitError();
  });

  private started = false;

  constructor() {
    effect(() => {
      const element = this.dialog().nativeElement;
      if (!this.started) {
        this.started = true;
        this.store.clearErrors();
        const user = this.user();
        this.codes.set([...user.costCenterCodes]);
        this.model.set({ limit: formatPlain(user.approvalLimit) });
        this.base = { codes: [...user.costCenterCodes], limit: user.approvalLimit };
        if (!element.open) {
          element.showModal();
        }
      }
    });
  }

  ngOnDestroy(): void {
    this.store.clearErrors();
  }

  /** Esc does nothing while a save runs. */
  protected onCancel(event: Event): void {
    if (this.store.saving()) {
      event.preventDefault();
    }
  }

  protected close(): void {
    this.dialog().nativeElement.close();
  }

  protected async save(): Promise<void> {
    this.tried.set(true);
    this.store.clearErrors();
    await submit(this.limitForm, async () => {
      const limit = parseRandInput(this.model().limit);
      if (limit === null) {
        return;
      }
      const outcome = await this.store.save(this.user(), this.codes(), limit);
      if (outcome === 'saved') {
        this.close();
      } else if (outcome === 'conflict') {
        this.rebaseOnLatest(limit);
        this.conflicted.set(true);
      }
    });
  }

  /** After a conflict: own changes on top of the latest values (see the class comment). */
  private rebaseOnLatest(typedLimit: number): void {
    const latest = this.store.users().find((u) => u.userId === this.user().userId) ?? this.user();
    const mine = this.codes();
    const added = mine.filter((code) => !this.base.codes.includes(code));
    const removed = this.base.codes.filter((code) => !mine.includes(code));
    this.codes.set([...latest.costCenterCodes.filter((code) => !removed.includes(code)), ...added.filter((code) => !latest.costCenterCodes.includes(code))]);
    if (typedLimit === this.base.limit) {
      this.model.set({ limit: formatPlain(latest.approvalLimit) });
    }
    this.base = { codes: [...latest.costCenterCodes], limit: latest.approvalLimit };
  }
}

/** 200000 as the box shows it at the start: plain digits, a comma only if there are cents. */
function formatPlain(amount: number): string {
  return Number.isInteger(amount) ? String(amount) : amount.toFixed(2).replace('.', ',');
}
