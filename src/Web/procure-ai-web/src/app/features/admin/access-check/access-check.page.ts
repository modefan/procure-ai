import { Component, computed, effect, inject, OnInit, signal, untracked } from '@angular/core';
import { Combobox, ComboboxPopup, ComboboxWidget } from '@angular/aria/combobox';
import { Listbox, Option } from '@angular/aria/listbox';
import { form, FormField } from '@angular/forms/signals';
import { ActivatedRoute, Router } from '@angular/router';
import { CheckStepDto } from '../../../api/approvals/models/check-step-dto';
import { BusyButton } from '../../../core/ui/busy-button';
import { AccessCheckStore, CheckAction, normaliseRequestNumber } from './access-check.store';

const ACTIONS: CheckAction[] = ['view', 'approve'];

/**
 * Screen 16: why can (or can't) this user do this to that request (needs `admin.authorization`). The Approvals service runs the same
 * rules the APIs use and lists them one by one. Only the rules marked "gating" decide Allowed or Denied; the routing lines
 * (who approvals would pick today) are shown apart as an explanation. The inputs live in the query string, so a link reproduces a check.
 */
@Component({
  selector: 'app-access-check-page',
  imports: [FormField, BusyButton, Combobox, ComboboxPopup, ComboboxWidget, Listbox, Option],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <h1 class="text-2xl font-bold tracking-tight text-slate-900">Access check</h1>
      <p class="mt-1 text-slate-600">Runs the same authorization rules the APIs use, step by step.</p>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.reload()">Try again</button>
        </div>
      }

      <form novalidate class="card mt-6 grid gap-4 md:grid-cols-2 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_minmax(0,1fr)_auto] lg:items-start" (submit)="$event.preventDefault()">
        <div class="relative">
          <label for="check-user" class="field-label">User</label>
          <input
            id="check-user"
            ngCombobox
            #combo="ngCombobox"
            type="text"
            autocomplete="off"
            placeholder="Type a name or email"
            class="field-input"
            [(value)]="userText"
            [(expanded)]="open"
            [attr.aria-invalid]="userError() ? 'true' : null"
            [attr.aria-describedby]="userError() ? 'check-user-error' : null"
          />
          <ng-template ngComboboxPopup [combobox]="combo">
            <div
              ngComboboxWidget
              ngListbox
              #list="ngListbox"
              [activeDescendant]="list.activeDescendant()"
              selectionMode="explicit"
              [(value)]="picked"
              aria-label="Users"
              class="absolute inset-x-0 z-20 mt-1 max-h-60 overflow-y-auto rounded-lg border border-slate-300 bg-white p-1 text-sm shadow-lg"
            >
              @for (user of matches(); track user.userId) {
                <div ngOption [value]="user.userId" [label]="user.displayName" class="user-option">
                  <span class="font-medium text-slate-900">{{ user.displayName }}</span>
                  <span class="min-w-0 truncate text-xs text-slate-600">{{ user.email }}</span>
                </div>
              } @empty {
                <div class="px-3 py-2 text-slate-600">No user matches.</div>
              }
            </div>
          </ng-template>
          @if (userError(); as message) {
            <p id="check-user-error" role="alert" class="field-error">{{ message }}</p>
          }
        </div>

        <div>
          <label for="check-action" class="field-label">Action</label>
          <select id="check-action" class="field-input" [formField]="checkForm.action">
            @for (action of actions; track action) {
              <option [value]="action">{{ action }}</option>
            }
          </select>
        </div>

        <div>
          <label for="check-request" class="field-label">Request</label>
          <input
            id="check-request"
            type="text"
            autocomplete="off"
            placeholder="e.g. PR-1001"
            class="field-input font-mono"
            [formField]="checkForm.request"
            [attr.aria-invalid]="requestError() ? 'true' : null"
            [attr.aria-describedby]="requestError() ? 'check-request-error' : null"
          />
          @if (requestError(); as message) {
            <p id="check-request-error" role="alert" class="field-error">{{ message }}</p>
          }
        </div>

        <div class="md:mt-6">
          <app-busy-button type="submit" [busy]="store.checking()" busyLabel="Checking…" (pressed)="run()">Check</app-busy-button>
        </div>
      </form>

      @if (store.outcome(); as outcome) {
        @switch (outcome.kind) {
          @case ('result') {
            <div class="card mt-6" data-testid="result" role="status">
              <div
                data-testid="verdict"
                class="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border px-4 py-3"
                [class]="outcome.check.allowed ? 'border-emerald-200 bg-emerald-50 text-emerald-900' : 'border-red-200 bg-red-50 text-red-900'"
              >
                <span class="text-xl font-bold whitespace-nowrap">{{ outcome.check.allowed ? '✓ Allowed' : '✗ Denied' }}</span>
                <span class="text-sm">
                  {{ outcome.check.userName }} {{ outcome.check.allowed ? 'may' : 'may not' }} {{ outcome.check.action }}
                  <span class="whitespace-nowrap">{{ outcome.check.requestNumber }}</span>
                </span>
              </div>

              <h2 class="mt-5 text-sm font-semibold tracking-wide text-slate-700 uppercase">Rules</h2>
              <ol class="mt-2 divide-y divide-slate-100 rounded-lg border border-slate-200" data-testid="rules">
                @for (step of gating(outcome.check.steps); track step.code) {
                  <li class="flex items-start gap-3 px-4 py-3 text-sm" [attr.data-step]="step.code" [attr.data-passed]="step.passed">
                    <span
                      class="mt-0.5 inline-flex size-5 shrink-0 items-center justify-center rounded-full text-xs font-bold"
                      [class]="step.passed ? 'bg-emerald-100 text-emerald-800' : 'bg-red-100 text-red-800'"
                      aria-hidden="true"
                    >
                      {{ step.passed ? '✓' : '✗' }}
                    </span>
                    <span class="text-slate-900">
                      <span class="sr-only">{{ step.passed ? 'Passed: ' : 'Failed: ' }}</span>{{ step.text }}
                    </span>
                  </li>
                }
              </ol>

              @let lines = explanation(outcome.check.steps);
              @if (lines.length > 0) {
                  <div data-testid="routing-explanation" class="mt-5">
                    <h2 class="text-sm font-semibold tracking-wide text-slate-700 uppercase">Routing explanation</h2>
                    <p class="mt-1 text-sm text-slate-600">Who approvals would pick today. This does not change the result above.</p>
                    <ul class="mt-2 space-y-2 rounded-lg border border-dashed border-slate-300 bg-slate-50 px-4 py-3">
                      @for (step of lines; track step.code) {
                        <li class="flex items-start gap-3 text-sm text-slate-700" [attr.data-step]="step.code" [attr.data-passed]="step.passed">
                          <span class="mt-0.5 inline-flex size-5 shrink-0 items-center justify-center rounded-full bg-slate-200 text-xs font-bold text-slate-700" aria-hidden="true">
                            {{ step.passed ? '✓' : 'i' }}
                          </span>
                          <span>{{ step.text }}</span>
                        </li>
                      }
                    </ul>
                  </div>
              }
            </div>
          }
          @case ('not-found') {
            <div class="mt-6 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900" role="alert" data-testid="not-found">{{ outcome.message }}</div>
          }
          @case ('error') {
            <div class="mt-6 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900" role="alert" data-testid="check-error">{{ outcome.message }}</div>
          }
        }
      }
    </section>
  `,
})
export class AccessCheckPage implements OnInit {
  protected readonly store = inject(AccessCheckStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly actions = ACTIONS;
  protected readonly userText = signal('');
  protected readonly open = signal(false);
  /** The listbox value: zero or one user ID. */
  protected readonly picked = signal<string[]>([]);
  private readonly selectedId = signal<string | null>(null);
  private readonly tried = signal(false);
  /** The inputs the shown verdict is for (user, action, request number); null before the first check. */
  private readonly checkedInputs = signal<string | null>(null);

  private readonly model = signal<{ action: string; request: string }>({ action: 'approve', request: '' });
  protected readonly checkForm = form(this.model);

  private readonly selectedUser = computed(() => this.store.users().find((u) => u.userId === this.selectedId()) ?? null);

  protected readonly matches = computed(() => {
    const query = this.userText().trim().toLowerCase();
    const users = this.store.users();
    if (query === '' || this.selectedUser()?.displayName.toLowerCase() === query) {
      return users;
    }
    return users.filter((u) => u.displayName.toLowerCase().includes(query) || u.email.toLowerCase().includes(query));
  });

  protected readonly userError = computed(() => (this.tried() && !this.selectedUser() ? 'Choose a user from the list.' : null));
  protected readonly requestError = computed(() => (this.tried() && normaliseRequestNumber(this.model().request) === null ? 'Enter a request number.' : null));

  constructor() {
    // Choosing an option fills the box with the name; typing something else forgets the choice.
    effect(() => {
      const id = this.picked()[0];
      if (id !== undefined && id !== untracked(this.selectedId)) {
        const user = untracked(this.store.users).find((u) => u.userId === id);
        this.selectedId.set(id);
        if (user) {
          this.userText.set(user.displayName);
        }
        this.open.set(false);
      }
    });
    // A verdict belongs to the inputs it was checked with: changing the user, the action or the request clears it (final review F8).
    effect(() => {
      const inputs = this.inputsKey(this.selectedId(), this.model().action, this.model().request);
      const checked = this.checkedInputs();
      if (checked !== null && inputs !== checked) {
        untracked(() => {
          this.checkedInputs.set(null);
          this.store.clearOutcome();
        });
      }
    });
    effect(() => {
      const text = this.userText();
      const user = untracked(this.selectedUser);
      if (user && user.displayName !== text) {
        this.selectedId.set(null);
        this.picked.set([]);
      }
    });
  }

  async ngOnInit(): Promise<void> {
    await this.store.load();
    const params = this.route.snapshot.queryParamMap;
    const action = params.get('action');
    const request = params.get('request');
    const user = this.store.users().find((u) => u.userId === params.get('user'));
    if (action && (ACTIONS as string[]).includes(action)) {
      this.model.update((m) => ({ ...m, action }));
    }
    if (request) {
      this.model.update((m) => ({ ...m, request }));
    }
    if (user) {
      this.selectedId.set(user.userId);
      this.picked.set([user.userId]);
      this.userText.set(user.displayName);
      this.open.set(false);
    }
    if (user && request) {
      await this.run();
    }
  }

  private inputsKey(userId: string | null, action: string, request: string): string {
    return JSON.stringify([userId, action, normaliseRequestNumber(request)]);
  }

  protected gating(steps: CheckStepDto[]): CheckStepDto[] {
    return steps.filter((s) => s.gating);
  }

  protected explanation(steps: CheckStepDto[]): CheckStepDto[] {
    return steps.filter((s) => !s.gating);
  }

  protected async run(): Promise<void> {
    this.tried.set(true);
    const user = this.selectedUser();
    const number = normaliseRequestNumber(this.model().request);
    if (!user || !number) {
      return;
    }
    const action = this.model().action as CheckAction;
    this.checkedInputs.set(this.inputsKey(user.userId, action, number));
    this.model.update((m) => ({ ...m, request: number }));
    this.router.navigate([], { queryParams: { user: user.userId, action, request: number }, replaceUrl: true });
    await this.store.check(user.userId, action, number);
  }
}
