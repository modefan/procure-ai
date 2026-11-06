import { Component, computed, inject, OnDestroy, OnInit } from '@angular/core';
import { HasUnsavedChanges } from '../../../core/auth/unsaved-changes.guard';
import { BusyButton } from '../../../core/ui/busy-button';
import { MatrixStore, roleLabel } from './matrix.store';

/** The areas of the screen, in order; a permission belongs to the area named by the part before its dot. */
const AREAS: { key: string; label: string }[] = [
  { key: 'requests', label: 'Requests' },
  { key: 'orders', label: 'Orders' },
  { key: 'invoices', label: 'Invoices' },
  { key: 'budgets', label: 'Budgets' },
  { key: 'assistant', label: 'Assistant' },
  { key: 'admin', label: 'Admin' },
];

/** What each permission lets a role do (spec 4.2). A permission added in code later shows without a description. */
const DESCRIPTIONS: Record<string, string> = {
  'requests.read': 'See purchase requests.',
  'requests.create': 'Raise purchase requests and edit own drafts.',
  'requests.approve': 'Approve or reject requests in their approval chain.',
  'orders.read': 'See purchase orders and their deliveries.',
  'orders.receive': 'Record goods received against an order.',
  'invoices.read': 'See invoices and their three-way match.',
  'invoices.create': 'Enter or scan supplier invoices.',
  'invoices.release-payment': 'Release a matched invoice for payment.',
  'budgets.read': 'See cost center budgets.',
  'budgets.manage': 'Change the budget of a cost center.',
  'assistant.use': 'Ask the policy assistant and scan invoices.',
  'admin.authorization': 'Edit this matrix.',
  'admin.users': 'Edit cost centers and approval limits of users.',
  'admin.policies': 'Manage the policy documents the assistant answers from.',
  'admin.system': 'See system status, access checks and data changes.',
};

interface Group {
  label: string;
  permissions: string[];
}

/**
 * Screen 14: which role has which permission (needs `admin.authorization`). Rows are grouped by area, columns are the roles; a
 * change is a draft until Save changes, which saves role by role (see `MatrixStore`). The permission column stays in place when
 * the matrix scrolls sideways on a phone, and so do the area labels.
 */
@Component({
  selector: 'app-matrix-page',
  imports: [BusyButton],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <h1 class="text-2xl font-bold tracking-tight text-slate-900">Authorization matrix</h1>
      <p class="mt-1 text-slate-600">Which role may do what. Changes take effect for everyone in your organisation.</p>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="retry()">Try again</button>
        </div>
      }
      @if (store.actionError(); as message) {
        <div role="alert" data-testid="action-error" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary px-3 py-1" (click)="store.clearActionError()">Dismiss</button>
        </div>
      }
      @if (store.changedElsewhere()) {
        <div role="status" data-testid="changed-elsewhere" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900">
          <span><strong class="font-semibold">Changed by another admin.</strong> Reload to see their version; your unsaved edits are lost.</span>
          <button type="button" class="btn-secondary px-3 py-1" (click)="store.reload()">Reload</button>
        </div>
      }
      @if (saved(); as names) {
        <div role="status" data-testid="saved" class="mt-6 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-900">
          <strong class="font-semibold">Saved.</strong> Changed: {{ names }}.
        </div>
      }

      @if (store.isLoading() && !store.matrix()) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading the authorization matrix</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (store.matrix()) {
        <div class="mt-6 overflow-x-auto rounded-xl border border-slate-200 bg-white shadow-sm" data-testid="matrix-scroller">
          <table class="w-full min-w-[44rem] border-separate border-spacing-0 text-left text-sm">
            <caption class="sr-only">Permissions by role. Tick a box to give the role that permission.</caption>
            <thead class="text-xs font-semibold tracking-wide text-slate-600 uppercase">
              <tr>
                <th scope="col" class="sticky left-0 z-10 w-52 border-b sm:w-72 border-slate-200 bg-slate-50 px-4 py-3 whitespace-nowrap">Permission</th>
                @for (role of store.roles(); track role) {
                  <th scope="col" class="border-b border-slate-200 bg-slate-50 px-3 py-3 text-center whitespace-nowrap">{{ label(role) }}</th>
                }
              </tr>
            </thead>
            @for (group of groups(); track group.label) {
              <tbody>
                <!-- The label sits in the sticky permission column (like the rows below it), so it stays in view on a sideways scroll in
                     every browser; the filler carries the band's colour under the role columns. -->
                <tr data-band>
                  <th scope="rowgroup" class="sticky left-0 z-10 border-b border-slate-100 bg-slate-100 px-4 py-2 text-xs font-semibold tracking-wide text-slate-700 uppercase">{{ group.label }}</th>
                  <td [attr.colspan]="store.roles().length" class="border-b border-slate-100 bg-slate-100"></td>
                </tr>
                @for (permission of group.permissions; track permission) {
                  <tr class="group" [attr.data-permission]="permission">
                    <th scope="row" class="sticky left-0 z-10 border-b border-slate-100 bg-white px-4 py-3 font-normal group-hover:bg-slate-50">
                      <code class="font-mono text-[0.8125rem] font-semibold whitespace-nowrap text-slate-900">{{ permission }}</code>
                      <span class="block text-xs text-slate-600">{{ describe(permission) }}</span>
                    </th>
                    @for (role of store.roles(); track role) {
                      <td class="border-b border-slate-100 px-3 py-3 text-center group-hover:bg-slate-50">
                        <input
                          type="checkbox"
                          class="size-5 cursor-pointer rounded border-slate-400 accent-indigo-700 disabled:cursor-not-allowed"
                          [checked]="has(role, permission)"
                          [disabled]="store.saving()"
                          [attr.aria-label]="label(role) + ': ' + permission"
                          (change)="store.toggle(role, permission)"
                        />
                      </td>
                    }
                  </tr>
                }
              </tbody>
            }
          </table>
        </div>

        <div class="mt-4 flex flex-wrap items-center gap-x-4 gap-y-2">
          <app-busy-button [busy]="store.saving()" busyLabel="Saving…" [disabled]="!store.dirty()" (pressed)="save()">Save changes</app-busy-button>
          <button type="button" class="btn-secondary disabled:cursor-not-allowed disabled:opacity-60" [disabled]="!store.dirty() || store.saving()" (click)="store.discardChanges()">Discard</button>
          @if (store.dirty()) {
            <span class="text-sm text-slate-600" data-testid="dirty-note">{{ store.changedRoles().length }} role{{ store.changedRoles().length === 1 ? '' : 's' }} changed, not saved yet.</span>
          }
        </div>
        <p class="mt-3 text-sm text-slate-600">Audited. Services pick up changes within seconds.</p>
        <p class="mt-1 text-sm text-slate-600">Permissions are defined in code; the role &rarr; permission mapping is stored in the Access service and editable here.</p>
      }
    </section>
  `,
})
export class MatrixPage implements OnInit, OnDestroy, HasUnsavedChanges {
  protected readonly store = inject(MatrixStore);
  protected readonly skeletonRows = [1, 2, 3, 4, 5, 6];

  protected readonly groups = computed<Group[]>(() => {
    const all = this.store.permissions();
    const known = AREAS.map((area) => ({ label: area.label, permissions: all.filter((p) => p.split('.')[0] === area.key) }));
    const other = all.filter((p) => !AREAS.some((area) => area.key === p.split('.')[0]));
    return [...known, { label: 'Other', permissions: other }].filter((g) => g.permissions.length > 0);
  });

  /** The roles saved by the last Save as "Requester, Manager", while nothing is waiting to be saved. */
  protected readonly saved = computed(() => {
    const roles = this.store.savedRoles();
    return roles.length > 0 && !this.store.dirty() && !this.store.actionError() ? roles.map(roleLabel).join(', ') : null;
  });

  protected label = roleLabel;

  protected describe(permission: string): string {
    return DESCRIPTIONS[permission] ?? '';
  }

  protected has(role: string, permission: string): boolean {
    return !!this.store.draft()[role]?.has(permission);
  }

  protected save(): void {
    void this.store.save();
  }

  protected retry(): void {
    void this.store.reload();
  }

  hasUnsavedChanges(): boolean {
    return this.store.dirty();
  }

  ngOnInit(): void {
    void this.store.load();
  }

  ngOnDestroy(): void {
    // Leaving with edits was confirmed (unsavedChangesGuard): they must not come back with the next visit.
    this.store.discardChanges();
  }
}
