import { Component, input } from '@angular/core';
import { AccordionContent, AccordionGroup, AccordionPanel, AccordionTrigger } from '@angular/aria/accordion';
import { formatRand } from '../../core/ui/money.pipe';
import { ToolStep } from './assistant.store';

/** The arguments of a step as an object; {} when they are not a JSON object (or were hidden by the server). */
function argumentsOf(step: ToolStep): Record<string, unknown> {
  try {
    const parsed: unknown = JSON.parse(step.arguments);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

const text = (value: unknown): string | null => (typeof value === 'string' && value.trim() !== '' ? value.trim() : null);

/** What the step did, in words: "Checked budget for IT-200". Unknown tools show their name. */
export function toolLabel(step: ToolStep): string {
  const args = argumentsOf(step);
  switch (step.name) {
    case 'get_budget':
      return text(args['costCenter']) ? `Checked budget for ${text(args['costCenter'])}` : 'Checked a budget';
    case 'get_request_status':
      return text(args['number']) ? `Checked status of ${text(args['number'])}` : 'Checked a request';
    case 'explain_approval_chain': {
      const amount = typeof args['amountInclVat'] === 'number' ? formatRand(args['amountInclVat']) : null;
      const costCenter = text(args['costCenter']);
      return amount && costCenter ? `Worked out the approvers for ${amount} in ${costCenter}` : 'Worked out the approvers';
    }
    case 'find_similar_purchases':
      return text(args['description']) ? `Looked for purchases like “${text(args['description'])}”` : 'Looked for similar purchases';
    case 'get_supplier':
      return text(args['name']) ? `Looked up supplier “${text(args['name'])}”` : 'Looked up a supplier';
    case 'search_policy':
      return text(args['query']) ? `Searched the policy for “${text(args['query'])}”` : 'Searched the policy';
    default:
      return step.name;
  }
}

/** The arguments, one per line, for the expanded row. */
export function toolArguments(step: ToolStep): string {
  const args = argumentsOf(step);
  const keys = Object.keys(args);
  return keys.length === 0 ? step.arguments : keys.map((k) => `${k}: ${typeof args[k] === 'string' ? args[k] : JSON.stringify(args[k])}`).join('\n');
}

/**
 * The tools an answer used, as disclosure rows (Angular Aria accordion, several open at once): "🔧 Checked budget for IT-200 ·
 * 120 ms ✓", a spinner while it runs, ✗ and the reason when it failed. Open, a row shows the arguments and the summary.
 */
@Component({
  selector: 'app-tool-steps',
  imports: [AccordionGroup, AccordionTrigger, AccordionPanel, AccordionContent],
  template: `
    <div ngAccordionGroup [multiExpandable]="true" class="space-y-1" data-testid="tool-steps">
      @for (step of steps(); track step.id) {
        <div class="rounded-lg border border-slate-200 bg-slate-50 text-sm" [attr.data-tool]="step.name" [attr.data-status]="step.status">
          <button
            ngAccordionTrigger
            [panel]="panel"
            type="button"
            class="tool-trigger flex w-full min-w-0 cursor-pointer items-center gap-2 rounded-lg px-3 py-1.5 text-left text-slate-700 hover:bg-slate-100"
          >
            <span aria-hidden="true">🔧</span>
            <span class="min-w-0 flex-1 truncate">
              {{ label(step) }}
              @if (step.status === 'failed' && step.summary) {
                <span class="text-red-800">· {{ step.summary }}</span>
              }
            </span>
            @if (step.durationMs !== null) {
              <span class="shrink-0 whitespace-nowrap text-slate-500">· {{ step.durationMs }} ms</span>
            }
            @switch (step.status) {
              @case ('running') {
                <span class="size-3.5 shrink-0 animate-spin rounded-full border-2 border-indigo-200 border-t-indigo-700 motion-reduce:animate-none" aria-hidden="true"></span>
                <span class="sr-only">running</span>
              }
              @case ('ok') {
                <span class="shrink-0 font-semibold text-emerald-700" aria-hidden="true">✓</span>
                <span class="sr-only">done</span>
              }
              @case ('failed') {
                <span class="shrink-0 font-semibold text-red-700" aria-hidden="true">✗</span>
                <span class="sr-only">failed</span>
              }
            }
            <span class="tool-chevron shrink-0 text-xs text-slate-500" aria-hidden="true">▾</span>
          </button>
          <div ngAccordionPanel #panel="ngAccordionPanel" class="border-t border-slate-200 px-3 py-2 inert:hidden">
            <ng-template ngAccordionContent>
              <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
                <dt class="font-semibold text-slate-600">Arguments</dt>
                <dd class="min-w-0"><pre class="font-mono break-words whitespace-pre-wrap text-slate-800">{{ args(step) }}</pre></dd>
                <dt class="font-semibold text-slate-600">Result</dt>
                <dd class="min-w-0 break-words text-slate-800">{{ step.summary ?? 'Still running…' }}</dd>
              </dl>
            </ng-template>
          </div>
        </div>
      }
    </div>
  `,
  styles: `
    .tool-trigger[aria-expanded='true'] .tool-chevron {
      transform: rotate(180deg);
    }
  `,
})
export class ToolSteps {
  readonly steps = input.required<ToolStep[]>();
  protected readonly label = toolLabel;
  protected readonly args = toolArguments;
}
