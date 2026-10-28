import { Component, computed, effect, HostListener, inject, input, OnInit, signal, untracked } from '@angular/core';
import { form, FormField, applyEach, disabled, max, maxLength, min, minLength, required, submit, validate } from '@angular/forms/signals';
import { Router, RouterLink } from '@angular/router';
import { HasUnsavedChanges } from '../../core/auth/unsaved-changes.guard';
import { ReferenceDataStore } from '../../core/reference/reference-data.store';
import { BusyButton } from '../../core/ui/busy-button';
import { formatRand, RandPipe } from '../../core/ui/money.pipe';
import { createApproversPreview } from './approvers-preview';
import { createBudgetCheck } from './budget-check';
import { RequestFormStore } from './request-form.store';
import { computeTotals, LineInput, MAX_QUANTITY, MAX_TITLE_LENGTH, priceLines, VAT_RATE_PERCENT } from './request-lines';

interface RequestFormModel {
  title: string;
  supplierId: string;
  costCenter: string;
  lines: LineInput[];
}

const emptyModel = (): RequestFormModel => ({ title: '', supplierId: '', costCenter: '', lines: [{ sku: '', quantity: 1 }] });

/**
 * Screens 5/6, the other half: a new purchase request (`/app/requests/new`) or the edit of a draft
 * (`/app/requests/:id/edit`). The server prices the lines from the supplier's catalog; the form shows the same prices
 * and the totals (VAT 15 % on the document total) while the user types. Leaving with edits asks first (`canDeactivate`
 * and `beforeunload`).
 */
@Component({
  selector: 'app-request-form-page',
  imports: [FormField, RouterLink, BusyButton, RandPipe],
  providers: [RequestFormStore],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <a routerLink="/app/requests" class="text-sm font-medium text-indigo-700 hover:underline">&larr; Purchase requests</a>
      <h1 class="mt-2 text-2xl font-bold tracking-tight text-slate-900">{{ heading() }}</h1>
      <p class="mt-1 text-slate-600">Prices come from the supplier's catalog. Amounts include VAT unless marked.</p>

      @if (isEdit() && baseline() === null && !store.error() && !notEditable()) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading the request</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (isEdit() && store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <a routerLink="/app/requests" class="btn-secondary">Back to requests</a>
        </div>
      } @else if (notEditable()) {
        <div class="card mt-6">
          <p class="font-semibold text-slate-900">Only drafts can be edited</p>
          <p class="mt-1 text-sm text-slate-600">{{ store.request()?.number }} is {{ store.request()?.status?.toLowerCase() }}.</p>
          <a [routerLink]="['/app/requests', store.request()?.id]" class="btn-secondary mt-4">Open the request</a>
        </div>
      } @else {
        <form class="mt-6 grid gap-6 lg:grid-cols-[minmax(0,1fr)_21rem]" novalidate (submit)="$event.preventDefault()">
          <div class="space-y-6">
            @if (store.generalError(); as message) {
              <div role="alert" class="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">{{ message }}</div>
            }

            <div class="card space-y-5">
              <div>
                <label for="title" class="field-label">Title</label>
                <input
                  id="title"
                  type="text"
                  class="field-input"
                  autocomplete="off"
                  [formField]="requestForm.title"
                  [attr.aria-invalid]="errorOf(requestForm.title(), 'title') ? 'true' : null"
                  [attr.aria-describedby]="errorOf(requestForm.title(), 'title') ? 'title-error' : null"
                />
                @if (errorOf(requestForm.title(), 'title'); as message) {
                  <p id="title-error" class="field-error">{{ message }}</p>
                }
              </div>

              <div class="grid gap-5 sm:grid-cols-2">
                <div>
                  <label for="supplier" class="field-label">Supplier</label>
                  <select
                    id="supplier"
                    class="field-input"
                    [formField]="requestForm.supplierId"
                    [attr.aria-invalid]="errorOf(requestForm.supplierId(), 'supplier') ? 'true' : null"
                    [attr.aria-describedby]="errorOf(requestForm.supplierId(), 'supplier') ? 'supplier-error' : null"
                  >
                    <option value="">Choose a supplier</option>
                    @for (supplier of suppliers(); track supplier.code) {
                      <option [value]="supplier.code">{{ supplier.name }}</option>
                    }
                  </select>
                  @if (errorOf(requestForm.supplierId(), 'supplier'); as message) {
                    <p id="supplier-error" class="field-error">{{ message }}</p>
                  }
                </div>
                <div>
                  <label for="cost-center" class="field-label">Cost center</label>
                  <select
                    id="cost-center"
                    class="field-input"
                    [formField]="requestForm.costCenter"
                    [attr.aria-invalid]="errorOf(requestForm.costCenter(), 'costCenter') ? 'true' : null"
                    [attr.aria-describedby]="errorOf(requestForm.costCenter(), 'costCenter') ? 'cost-center-error' : null"
                  >
                    <option value="">Choose a cost center</option>
                    @for (center of ref.costCenters(); track center.code) {
                      <option [value]="center.code">{{ center.code }} &middot; {{ center.name }}</option>
                    }
                  </select>
                  @if (errorOf(requestForm.costCenter(), 'costCenter'); as message) {
                    <p id="cost-center-error" class="field-error">{{ message }}</p>
                  }
                </div>
              </div>
            </div>

            <div class="card">
              <h2 class="text-base font-semibold text-slate-900">Items</h2>
              @if (model().supplierId === '') {
                <p class="mt-3 text-sm text-slate-600">Choose a supplier to pick items from its catalog.</p>
              } @else if (catalogError(); as message) {
                <p role="alert" class="mt-3 text-sm text-red-800">{{ message }}</p>
              } @else {
                <div class="mt-3 hidden grid-cols-[minmax(0,1fr)_7.5rem_8rem_8.5rem_2.5rem] gap-3 border-b border-slate-200 pb-2 text-xs font-semibold uppercase tracking-wide text-slate-600 md:grid">
                  <span>Item</span>
                  <span>Qty</span>
                  <span class="text-right whitespace-nowrap">Unit price</span>
                  <span class="text-right">Total</span>
                  <span></span>
                </div>
                <ul class="divide-y divide-slate-100">
                  @for (line of requestForm.lines; track $index; let i = $index) {
                    <li
                      class="grid grid-cols-[7.5rem_minmax(0,1fr)] items-start gap-x-3 gap-y-2 py-3 md:grid-cols-[minmax(0,1fr)_7.5rem_8rem_8.5rem_2.5rem]"
                      [attr.data-testid]="'line-' + i"
                    >
                      <div class="col-span-2 min-w-0 md:col-span-1">
                        <select
                          class="field-input"
                          [attr.aria-label]="'Item, line ' + (i + 1)"
                          [formField]="line.sku"
                          [attr.aria-invalid]="errorOf(line.sku(), 'lines.' + i + '.sku') ? 'true' : null"
                          [attr.aria-describedby]="errorOf(line.sku(), 'lines.' + i + '.sku') ? 'line-' + i + '-sku-error' : null"
                        >
                          <option value="">Choose an item</option>
                          @for (item of itemOptions(); track item.sku) {
                            <option [value]="item.sku">{{ item.description }} ({{ item.sku }})</option>
                          }
                        </select>
                        @if (errorOf(line.sku(), 'lines.' + i + '.sku'); as message) {
                          <p [id]="'line-' + i + '-sku-error'" class="field-error">{{ message }}</p>
                        }
                      </div>
                      <div>
                        <input
                          type="number"
                          inputmode="numeric"
                          class="field-input text-right tabular-nums [appearance:textfield] [&::-webkit-inner-spin-button]:appearance-none"
                          [attr.aria-label]="'Quantity, line ' + (i + 1)"
                          [formField]="line.quantity"
                          [attr.aria-invalid]="errorOf(line.quantity(), 'lines.' + i + '.quantity') ? 'true' : null"
                          [attr.aria-describedby]="errorOf(line.quantity(), 'lines.' + i + '.quantity') ? 'line-' + i + '-quantity-error' : null"
                        />
                        @if (errorOf(line.quantity(), 'lines.' + i + '.quantity'); as message) {
                          <p [id]="'line-' + i + '-quantity-error'" class="field-error">{{ message }}</p>
                        }
                      </div>
                      <div class="min-w-0 space-y-1 md:contents">
                      <p class="flex items-baseline justify-between gap-2 text-sm whitespace-nowrap tabular-nums text-slate-700 md:block md:pt-2 md:text-right">
                        <span class="text-xs text-slate-500 md:hidden">Unit price</span>
                        <span>{{ money(priced()[i]?.unitPrice) }}</span>
                      </p>
                      <p class="flex items-baseline justify-between gap-2 text-sm font-medium whitespace-nowrap tabular-nums text-slate-900 md:block md:pt-2 md:text-right">
                        <span class="text-xs font-normal text-slate-500 md:hidden">Total</span>
                        <span>{{ money(priced()[i]?.total) }}</span>
                      </p>
                      </div>
                      <div class="col-span-2 flex justify-end md:col-span-1">
                        <button
                          type="button"
                          class="btn-ghost px-2 py-1 text-lg leading-none text-slate-700 disabled:cursor-not-allowed disabled:opacity-40"
                          [attr.aria-label]="'Remove line ' + (i + 1)"
                          [disabled]="model().lines.length === 1 || store.isBusy()"
                          (click)="removeLine(i)"
                        >
                          <span aria-hidden="true">&times;</span>
                          <span class="md:hidden">Remove</span>
                        </button>
                      </div>
                    </li>
                  }
                </ul>
                @if (errorOf(requestForm.lines(), 'lines'); as message) {
                  <p class="field-error">{{ message }}</p>
                }
                <button type="button" class="btn-secondary mt-3" [disabled]="store.isBusy() || model().lines.length >= maxLines" (click)="addLine()">+ Add item</button>

                <dl class="mt-5 space-y-1 border-t border-slate-200 pt-4 text-sm" data-testid="totals">
                  <div class="flex justify-between gap-4"><dt class="text-slate-600">Total excl. VAT</dt><dd class="whitespace-nowrap tabular-nums">{{ totals().exclVat | rand }}</dd></div>
                  <div class="flex justify-between gap-4"><dt class="text-slate-600">VAT {{ vatRate }} %</dt><dd class="whitespace-nowrap tabular-nums">{{ totals().vat | rand }}</dd></div>
                  <div class="flex justify-between gap-4 text-base font-semibold text-slate-900"><dt>Total incl. VAT</dt><dd class="whitespace-nowrap tabular-nums">{{ totals().inclVat | rand }}</dd></div>
                </dl>
              }
            </div>

            <div class="flex flex-wrap items-center justify-end gap-3">
              <p class="mr-auto text-sm text-emerald-800" role="status" aria-live="polite">{{ savedMessage() }}</p>
              <a routerLink="/app/requests" class="btn-ghost">Cancel</a>
              <app-busy-button
                data-testid="save-draft"
                variant="secondary"
                [busy]="store.busy() === 'saving'"
                [disabled]="store.isBusy()"
                busyLabel="Saving…"
                (pressed)="saveDraft()"
              >Save draft</app-busy-button>
              <app-busy-button
                data-testid="submit"
                [busy]="store.busy() === 'submitting'"
                [disabled]="store.isBusy() || approvers.state().kind === 'nobody'"
                busyLabel="Submitting…"
                (pressed)="submitRequest()"
              >Submit request</app-busy-button>
            </div>
          </div>

          <aside class="space-y-4 lg:sticky lg:top-6 lg:self-start">
            <div class="card" data-testid="budget" aria-live="polite">
              <h2 class="field-label">Budget check</h2>
              @switch (budget.state().kind) {
                @case ('within') {
                  <p class="mt-2 text-sm text-slate-700">{{ model().costCenter }} available: <b class="whitespace-nowrap tabular-nums">{{ availableRand() }}</b></p>
                  <p class="mt-1 font-medium text-emerald-800">&check; Within budget</p>
                }
                @case ('over') {
                  <p class="mt-2 text-sm text-slate-700">{{ model().costCenter }} available: <b class="whitespace-nowrap tabular-nums">{{ availableRand() }}</b></p>
                  <p class="mt-1 font-medium text-red-800">&cross; Over budget by {{ overByRand() }}</p>
                }
                @case ('checking') {
                  <p class="mt-2 text-sm text-slate-600">Checking&hellip;</p>
                }
                @case ('unknown') {
                  <p class="mt-2 text-sm text-amber-800">The budget could not be checked right now. It is checked again when you submit.</p>
                }
                @default {
                  <p class="mt-2 text-sm text-slate-600">Choose a cost center and items to check the budget.</p>
                }
              }
            </div>

            <div class="card" data-testid="approvers" aria-live="polite">
              <h2 class="field-label">Approvals needed</h2>
              @switch (approvers.state().kind) {
                @case ('steps') {
                  <ol class="mt-2 space-y-1 text-sm text-slate-800">
                    @for (step of approverSteps(); track step.stepNo) {
                      <li>{{ step.stepNo }}. {{ levelName(step.level) }} ({{ step.approverNames.join(', ') }})</li>
                    } @empty {
                      <li class="text-slate-600">No approval needed.</li>
                    }
                  </ol>
                }
                @case ('nobody') {
                  <p class="mt-2 text-sm font-medium text-red-800">{{ nobodyReason() }}</p>
                  <p class="mt-1 text-sm text-slate-600">The request cannot be submitted until an approver can approve this amount.</p>
                }
                @case ('checking') {
                  <p class="mt-2 text-sm text-slate-600">Checking&hellip;</p>
                }
                @case ('unknown') {
                  <p class="mt-2 text-sm text-amber-800">The approvers could not be looked up right now.</p>
                }
                @default {
                  <p class="mt-2 text-sm text-slate-600">Shown once there is a total.</p>
                }
              }
            </div>
          </aside>
        </form>
      }
    </section>
  `,
})
export class RequestFormPage implements OnInit, HasUnsavedChanges {
  /** The route's `:id`; empty on `/app/requests/new`. */
  readonly id = input<string>();

  protected readonly store = inject(RequestFormStore);
  protected readonly ref = inject(ReferenceDataStore);
  private readonly router = inject(Router);

  protected readonly skeletonRows = [1, 2, 3, 4];
  protected readonly vatRate = VAT_RATE_PERCENT;
  protected readonly maxLines = 100;

  protected readonly model = signal<RequestFormModel>(emptyModel());
  protected readonly isEdit = computed(() => !!this.id());
  protected readonly heading = computed(() => (this.isEdit() ? `Edit ${this.store.request()?.number ?? 'request'}` : 'New purchase request'));
  protected readonly notEditable = computed(() => this.isEdit() && this.store.isLoaded() && this.store.request()?.status !== 'Draft');

  protected readonly requestForm = form(this.model, (p) => {
    required(p.title, { message: 'Enter a title' });
    validate(p.title, ({ value }) => (value() !== '' && value().trim() === '' ? { kind: 'required', message: 'Enter a title' } : undefined));
    maxLength(p.title, MAX_TITLE_LENGTH, { message: `Use at most ${MAX_TITLE_LENGTH} characters` });
    required(p.supplierId, { message: 'Choose a supplier' });
    required(p.costCenter, { message: 'Choose a cost center' });
    minLength(p.lines, 1, { message: 'Add at least one item' });
    applyEach(p.lines, (line) => {
      required(line.sku, { message: 'Choose an item' });
      min(line.quantity, 1, { message: 'Enter at least 1' });
      max(line.quantity, MAX_QUANTITY, { message: `Enter at most ${MAX_QUANTITY.toLocaleString('en-ZA')}` });
      validate(line.quantity, ({ value }) => (Number.isInteger(value()) ? undefined : { kind: 'whole', message: 'Enter a whole number' }));
    });
    // The API cannot change supplier or cost center of an existing draft.
    disabled(p.supplierId, () => this.isEdit());
    disabled(p.costCenter, () => this.isEdit());
  });

  protected readonly suppliers = computed(() => this.ref.suppliers().filter((s) => s.active || s.code === this.model().supplierId));
  protected readonly catalog = computed(() => this.ref.catalogItems(this.model().supplierId));
  protected readonly catalogError = computed(() => (this.model().supplierId === '' ? null : this.ref.errorOf(`catalog:${this.model().supplierId}`)));
  /** The catalog plus any saved line whose SKU the catalog no longer lists, so the select can still show it. */
  protected readonly itemOptions = computed(() => {
    const catalog = this.catalog();
    const chosen = new Set(this.model().lines.map((l) => l.sku));
    // A retired item is offered only where a line already holds it.
    const offered = catalog.filter((c) => c.active || chosen.has(c.sku));
    const missing = (this.store.request()?.lines ?? []).filter((l) => !catalog.some((c) => c.sku === l.sku));
    return [...offered, ...missing.map((l) => ({ sku: l.sku, description: l.description }))];
  });
  protected readonly priced = computed(() => priceLines(this.model().lines, this.catalog(), this.store.request()?.lines ?? []));
  protected readonly totals = computed(() => computeTotals(this.priced()));

  protected readonly budget = createBudgetCheck(() => this.model().costCenter, () => this.totals().inclVat);
  protected readonly approvers = createApproversPreview(() => this.model().costCenter, () => this.totals().inclVat);

  protected readonly availableRand = computed(() => {
    const s = this.budget.state();
    return s.kind === 'within' || s.kind === 'over' ? formatRand(s.available) : '';
  });
  protected readonly overByRand = computed(() => {
    const s = this.budget.state();
    return s.kind === 'over' ? formatRand(s.overBy) : '';
  });
  protected readonly approverSteps = computed(() => {
    const s = this.approvers.state();
    return s.kind === 'steps' ? s.steps : [];
  });
  protected readonly nobodyReason = computed(() => {
    const s = this.approvers.state();
    return s.kind === 'nobody' ? s.reason : '';
  });

  protected readonly savedMessage = signal('');
  /** The model as last loaded or saved; null until the form has its starting values. */
  protected readonly baseline = signal<string | null>(null);
  private readonly dirty = computed(() => this.baseline() !== null && JSON.stringify(this.model()) !== this.baseline());
  private previousSupplier = '';

  constructor() {
    // Typing clears the server's complaints about the old values.
    effect(() => {
      this.model();
      untracked(() => {
        this.store.clearErrors();
        this.savedMessage.set('');
      });
    });
    // A new supplier means the lines' SKUs belong to another catalog: clear them. The catalog of any supplier loads once.
    effect(() => {
      const supplier = this.model().supplierId;
      untracked(() => {
        if (supplier === this.previousSupplier) {
          return;
        }
        if (this.previousSupplier !== '') {
          this.model.update((m) => ({ ...m, lines: m.lines.map((l) => ({ ...l, sku: '' })) }));
        }
        this.previousSupplier = supplier;
        if (supplier !== '') {
          void this.ref.loadCatalogItems(supplier);
        }
      });
    });
  }

  async ngOnInit(): Promise<void> {
    const id = this.id();
    const reference = Promise.all([this.ref.loadMe(), this.ref.loadSuppliers()]);
    if (id) {
      await Promise.all([reference, this.store.load(id)]);
      const request = this.store.request();
      if (request && request.status === 'Draft') {
        await this.ref.loadCatalogItems(request.supplierId);
        this.start({
          title: request.title,
          supplierId: request.supplierId,
          costCenter: request.costCenterCode,
          lines: request.lines.map((l) => ({ sku: l.sku, quantity: l.quantity })),
        });
      }
      return;
    }
    await reference;
    const centers = this.ref.costCenters();
    this.start({ ...emptyModel(), costCenter: centers.length === 1 ? centers[0].code : '' });
  }

  hasUnsavedChanges(): boolean {
    return this.dirty();
  }

  @HostListener('window:beforeunload', ['$event'])
  protected warnBeforeUnload(event: BeforeUnloadEvent): void {
    if (this.dirty()) {
      event.preventDefault();
    }
  }

  /** "DepartmentHead" to "Department head". */
  protected levelName(level: string): string {
    const words = level.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
    return words.charAt(0).toUpperCase() + words.slice(1);
  }

  protected money(amount: number | null | undefined): string {
    return amount === null || amount === undefined ? '—' : formatRand(amount);
  }

  protected addLine(): void {
    this.model.update((m) => ({ ...m, lines: [...m.lines, { sku: '', quantity: 1 }] }));
  }

  protected removeLine(index: number): void {
    this.model.update((m) => ({ ...m, lines: m.lines.filter((_, i) => i !== index) }));
  }

  /** The message to show under a field: its own validation (once touched), else what the server said about it. */
  protected errorOf(state: { touched(): boolean; errors(): readonly { message?: string }[] }, serverPath: string): string | null {
    if (state.touched() && state.errors().length > 0) {
      return state.errors()[0].message ?? 'Check this value';
    }
    return this.store.fieldErrors()[serverPath]?.[0] ?? null;
  }

  protected async saveDraft(): Promise<void> {
    await submit(this.requestForm, async () => {
      const saved = await this.store.saveDraft(this.draft());
      if (!saved) {
        return;
      }
      this.markSaved();
      this.savedMessage.set('Draft saved.');
      if (!this.isEdit()) {
        await this.router.navigate(['/app/requests', saved.id, 'edit'], { replaceUrl: true });
      }
    });
  }

  protected async submitRequest(): Promise<void> {
    await submit(this.requestForm, async () => {
      const submitted = await this.store.submit(this.draft());
      if (!submitted) {
        // The draft may have been saved before the submit failed; only then are the edits safe.
        if (this.store.savedMatches(this.draft())) {
          this.markSaved();
        }
        return;
      }
      this.markSaved();
      await this.router.navigate(['/app/requests', submitted.id]);
    });
  }

  private start(model: RequestFormModel): void {
    this.previousSupplier = model.supplierId;
    this.model.set(model);
    this.markSaved();
  }

  private markSaved(): void {
    this.baseline.set(JSON.stringify(this.model()));
  }

  private draft() {
    const m = this.model();
    return { title: m.title.trim(), supplierId: m.supplierId, costCenterCode: m.costCenter, lines: m.lines.map((l) => ({ sku: l.sku, quantity: l.quantity })) };
  }
}
