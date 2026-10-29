import { KeepRandPipe } from '../../core/ui/money.pipe';
import { Component, computed, input } from '@angular/core';
import { BusinessEventDto } from '../../api/audit/models/business-event-dto';
import { formatWhen } from './workflow-timeline';

/** The business history of one request, newest first: when, what happened (the sentence Audit wrote), and the event type. */
@Component({
  selector: 'app-audit-trail',
  imports: [KeepRandPipe],
  template: `
    @if (rows().length === 0) {
      <p class="text-sm text-slate-600">Nothing has happened to this request yet.</p>
    } @else {
      <table class="w-full text-left text-sm" aria-label="Audit trail">
        <thead class="sr-only">
          <tr>
            <th scope="col">When</th>
            <th scope="col">What happened</th>
          </tr>
        </thead>
        <tbody class="divide-y divide-slate-100">
          @for (row of rows(); track $index) {
            <tr class="align-top">
              <td class="py-2 pr-3 whitespace-nowrap text-slate-500 tabular-nums">
                <time [attr.datetime]="row.occurredAt">{{ when(row.occurredAt) }}</time>
              </td>
              <td class="py-2">
                <span class="block break-words text-slate-800">{{ row.summary | keepRand }}</span>
                <span class="block font-mono text-xs break-all text-slate-500">{{ row.type }}</span>
              </td>
            </tr>
          }
        </tbody>
      </table>
    }
  `,
})
export class AuditTrail {
  readonly events = input.required<BusinessEventDto[]>();
  protected readonly rows = computed(() => [...this.events()].sort((a, b) => b.occurredAt.localeCompare(a.occurredAt)));
  protected readonly when = formatWhen;
}
