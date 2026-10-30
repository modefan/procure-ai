import { Component, computed, input } from '@angular/core';
import { FieldChangeDto } from '../../api/audit/models/field-change-dto';

/** What the Audit service stores in place of a `[Sensitive]` column's value (old and new alike). */
const MASKED = '***';

interface DiffRow {
  column: string;
  old: string | null;
  new: string | null;
  oldMasked: boolean;
  newMasked: boolean;
}

/**
 * The field-level change of one database row: `column  old → new` for an update, the new values with a "new" badge for an
 * insert and the old ones with "removed" for a delete. Sensitive columns arrive masked from the server and show as `•••`;
 * an empty value shows as "empty".
 */
@Component({
  selector: 'app-field-diff',
  template: `
    <div class="space-y-1" data-testid="field-diff">
      @if (operation() === 'Insert') {
        <span class="badge-live" data-testid="diff-operation">new</span>
      } @else if (operation() === 'Delete') {
        <span class="badge-error" data-testid="diff-operation">removed</span>
      }
      @if (rows().length === 0) {
        <p class="text-sm text-slate-600">No column values were recorded.</p>
      }
      <dl class="grid grid-cols-[minmax(0,auto)_minmax(0,1fr)] gap-x-4 gap-y-1 text-sm">
        @for (row of rows(); track row.column) {
          <dt class="font-mono text-xs break-all text-slate-600 sm:text-sm" [attr.data-column]="row.column">{{ row.column }}</dt>
          <dd class="min-w-0 break-words text-slate-900" [attr.data-value]="row.column">
            @if (operation() === 'Update') {
              <span [class]="valueClass(row.old, row.oldMasked) + ' line-through decoration-slate-400'" data-part="old">{{ show(row.old, row.oldMasked) }}</span>
              <span class="text-slate-500">&nbsp;&rarr;&nbsp;</span>
              <span [class]="valueClass(row.new, row.newMasked)" data-part="new">{{ show(row.new, row.newMasked) }}</span>
            } @else if (operation() === 'Delete') {
              <span [class]="valueClass(row.old, row.oldMasked)" data-part="old">{{ show(row.old, row.oldMasked) }}</span>
            } @else {
              <span [class]="valueClass(row.new, row.newMasked)" data-part="new">{{ show(row.new, row.newMasked) }}</span>
            }
          </dd>
        }
      </dl>
    </div>
  `,
})
export class FieldDiff {
  readonly fields = input.required<FieldChangeDto[]>();
  /** `Insert`, `Update` or `Delete`. */
  readonly operation = input.required<string>();

  protected readonly rows = computed<DiffRow[]>(() =>
    this.fields().map((f) => ({ column: f.column, old: f.old, new: f.new, oldMasked: f.old === MASKED, newMasked: f.new === MASKED })),
  );

  protected show(value: string | null, masked: boolean): string {
    return masked ? '•••' : (value ?? 'empty');
  }

  protected valueClass(value: string | null, masked: boolean): string {
    return value === null && !masked ? 'text-slate-500 italic' : masked ? 'tracking-widest text-slate-700' : '';
  }
}
