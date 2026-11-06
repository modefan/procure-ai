import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { RandPipe } from '../../../core/ui/money.pipe';
import { roleLabel } from '../matrix/matrix.store';
import { UserEditDialog } from './user-edit-dialog';
import { UsersStore } from './users.store';

/**
 * Screen 15: who works in which cost centers and up to what amount they approve (needs `admin.users`). Roles are shown, not
 * edited: they are managed in Keycloak. Edit opens a dialog for the cost centers and the approval limit (see `UserEditDialog`).
 */
@Component({
  selector: 'app-users-page',
  imports: [RandPipe, UserEditDialog],
  template: `
    <section class="mx-auto max-w-6xl px-4 py-8">
      <h1 class="text-2xl font-bold tracking-tight text-slate-900">Users, cost centers and limits</h1>
      <p class="mt-1 text-slate-600">Which cost centers a user works in and the highest amount they may approve.</p>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 flex flex-wrap items-center justify-between gap-3 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">
          <span>{{ message }}</span>
          <button type="button" class="btn-secondary" (click)="store.reload()">Try again</button>
        </div>
      }

      @if (store.isLoading() && store.users().length === 0) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading users</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (!store.error() || store.users().length > 0) {
        <div class="mt-6 overflow-x-auto rounded-xl border border-slate-200 bg-white shadow-sm" data-testid="users">
          <!-- Below lg the table stacks into cards (display: block), which drops its table semantics; the explicit roles keep them. -->
          <table role="table" class="w-full text-left text-sm lg:min-w-[52rem] max-lg:block">
            <caption class="sr-only">Users with their roles, cost centers and approval limit</caption>
            <thead role="rowgroup" class="border-b border-slate-200 bg-slate-50 text-xs font-semibold tracking-wide text-slate-600 uppercase max-lg:sr-only">
              <tr role="row">
                <th scope="col" role="columnheader" class="px-4 py-3">User</th>
                <th scope="col" role="columnheader" class="px-4 py-3">Roles <span class="font-normal tracking-normal text-slate-500 normal-case">(managed in Keycloak)</span></th>
                <th scope="col" role="columnheader" class="px-4 py-3">Cost centers</th>
                <th scope="col" role="columnheader" class="px-4 py-3 text-right">Approval limit</th>
                <th scope="col" role="columnheader" class="px-4 py-3"><span class="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody role="rowgroup" class="divide-y divide-slate-100 max-lg:block">
              @for (user of store.users(); track user.userId) {
                <tr role="row" [attr.data-user]="user.userId" class="max-lg:relative max-lg:block max-lg:space-y-2 max-lg:px-4 max-lg:py-4">
                  <td role="cell" class="px-4 py-3 max-lg:p-0 max-lg:pr-16">
                    <div class="font-medium text-slate-900">{{ user.displayName }}</div>
                    <div class="text-xs break-all text-slate-600">{{ user.email }}</div>
                  </td>
                  <td role="cell" class="px-4 py-3 max-lg:block max-lg:p-0">
                    <div class="flex flex-wrap gap-1">
                      @for (role of user.roles; track role) {
                        <span class="badge-muted">{{ label(role) }}</span>
                      }
                      @if (user.isFinanceApprover) {
                        <span class="badge-live">Finance approver</span>
                      }
                    </div>
                  </td>
                  <td role="cell" class="cell-label px-4 py-3 max-lg:block max-lg:p-0" data-label="Cost centers">
                    @if (user.costCenterCodes.length > 0) {
                      <div class="flex flex-wrap gap-x-2 gap-y-1">
                        @for (code of user.costCenterCodes; track code) {
                          <code class="font-mono text-[0.8125rem] whitespace-nowrap text-slate-800">{{ code }}</code>
                        }
                      </div>
                    } @else {
                      <span class="text-slate-500">None</span>
                    }
                  </td>
                  <td role="cell" class="cell-label px-4 py-3 text-right whitespace-nowrap tabular-nums text-slate-900 max-lg:block max-lg:p-0 max-lg:text-left" data-label="Approval limit">{{ user.approvalLimit | rand }}</td>
                  <td role="cell" class="px-4 py-3 text-right max-lg:absolute max-lg:top-4 max-lg:right-4 max-lg:p-0">
                    <button type="button" class="btn-secondary px-3 py-1" [attr.aria-label]="'Edit ' + user.displayName" (click)="editing.set(user.userId)">Edit</button>
                  </td>
                </tr>
              } @empty {
                <tr role="row">
                  <td role="cell" colspan="5" class="px-4 py-6 text-center text-slate-600">No users yet.</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <h2 class="mt-10 text-lg font-semibold text-slate-900">Cost centers</h2>
        <div class="mt-3 overflow-x-auto rounded-xl border border-slate-200 bg-white shadow-sm" data-testid="cost-centers">
          <table class="w-full min-w-[44rem] text-left text-sm">
            <caption class="sr-only">Cost centers with their department, manager and department head</caption>
            <thead class="border-b border-slate-200 bg-slate-50 text-xs font-semibold tracking-wide text-slate-600 uppercase">
              <tr>
                <th scope="col" class="px-4 py-3">Code</th>
                <th scope="col" class="px-4 py-3">Name</th>
                <th scope="col" class="px-4 py-3">Department</th>
                <th scope="col" class="px-4 py-3">Manager</th>
                <th scope="col" class="px-4 py-3">Department head</th>
              </tr>
            </thead>
            <tbody class="divide-y divide-slate-100">
              @for (center of store.costCenters(); track center.code) {
                <tr [attr.data-cost-center]="center.code">
                  <td class="px-4 py-3"><code class="font-mono text-[0.8125rem] font-semibold whitespace-nowrap text-slate-900">{{ center.code }}</code></td>
                  <td class="px-4 py-3 text-slate-900">{{ center.name }}</td>
                  <td class="px-4 py-3 text-slate-700">{{ center.departmentName }}</td>
                  <td class="px-4 py-3 text-slate-700">{{ center.managerName ?? 'None' }}</td>
                  <td class="px-4 py-3 text-slate-700">{{ center.departmentHeadName ?? 'None' }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }

      @if (editingUser(); as user) {
        <app-user-edit-dialog [user]="user" [costCenters]="store.costCenters()" (closed)="editing.set(null)" />
      }
    </section>
  `,
})
export class UsersPage implements OnInit {
  protected readonly store = inject(UsersStore);
  protected readonly skeletonRows = [1, 2, 3, 4, 5];
  protected readonly editing = signal<string | null>(null);
  protected readonly editingUser = computed(() => this.store.users().find((u) => u.userId === this.editing()) ?? null);
  protected label = roleLabel;

  ngOnInit(): void {
    void this.store.load();
  }
}
