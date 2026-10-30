import { Component, computed, input } from '@angular/core';
import { DataChangeDto } from '../../api/audit/models/data-change-dto';

/** The service behind each captured database (Debezium's database name), as the screens name it. */
export const SERVICE_LABELS: Record<string, string> = {
  requestsdb: 'Requests',
  approvalsdb: 'Approvals',
  budgetsdb: 'Budgets',
  purchasingdb: 'Purchasing',
  invoicingdb: 'Invoicing',
  accessdb: 'Access',
  assistantdb: 'Assistant',
  suppliersdb: 'Suppliers',
  assistantchatdb: 'Assistant chat',
};

/** "Requests" for `requestsdb`; an unknown database shows as it is. */
export const serviceLabel = (database: string): string => SERVICE_LABELS[database] ?? database;

/**
 * Who made a data change: the user's name (the ID when the name is not known), an "outside the app" badge for a change no
 * app transaction explains (manual SQL, scripts), or "pending" while the audit is still matching it to the app's own record.
 */
@Component({
  selector: 'app-data-change-who',
  template: `
    @switch (change().attribution) {
      @case ('OutsideApp') {
        <span class="badge-error whitespace-nowrap" data-testid="who-outside">outside the app</span>
      }
      @case ('Pending') {
        <span class="badge-muted whitespace-nowrap" data-testid="who-pending">pending</span>
      }
      @default {
        <span class="break-words" data-testid="who-user">{{ name() }}</span>
      }
    }
  `,
})
export class DataChangeWho {
  readonly change = input.required<DataChangeDto>();
  /** user id to display name; empty when the names could not be read. */
  readonly names = input<ReadonlyMap<string, string>>(new Map());

  protected readonly name = computed(() => {
    const change = this.change();
    const id = change.userId ?? change.actorId;
    return id ? (this.names().get(id) ?? id) : 'the app';
  });
}
