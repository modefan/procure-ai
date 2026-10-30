import { Component, input } from '@angular/core';
import { StreamSource } from './answer-stream';

/** "Procurement Policy §4.2 Approval thresholds" for a policy section; "PR-0982 · Laptops for IT" for a past purchase. */
export function sourceLabel(source: StreamSource): string {
  if (source.type === 'Purchase') {
    return source.reference ? `${source.reference} · ${source.title}` : source.title;
  }
  const section = [...source.sectionPath].reverse().find((s) => s.trim() !== '' && s !== source.title);
  if (!section) {
    return source.title;
  }
  return /^\d/.test(section) ? `${source.title} §${section}` : `${source.title} › ${section}`;
}

/** The full place of a source, for the tooltip. */
export function sourcePath(source: StreamSource): string {
  return [source.title, ...source.sectionPath.filter((s) => s !== source.title)].join(' › ');
}

/** The sources an answer cites, as chips: 📘 a policy section, 🧾 a past purchase. The S1… marker matches the [S1] in the text. */
@Component({
  selector: 'app-source-chips',
  template: `
    <ul class="flex flex-wrap gap-1.5" aria-label="Sources" data-testid="sources">
      @for (source of sources(); track source.ref) {
        <li
          class="inline-flex max-w-full min-w-0 items-center gap-1.5 rounded-full bg-white px-2.5 py-1 text-xs text-slate-700 ring-1 ring-slate-200 ring-inset"
          [title]="path(source)"
          [attr.data-source]="source.ref"
        >
          <span class="shrink-0 font-mono text-[0.7rem] font-semibold text-slate-500">{{ source.ref }}</span>
          <span aria-hidden="true" class="shrink-0">{{ source.type === 'Purchase' ? '🧾' : '📘' }}</span>
          <span class="sr-only">{{ source.type === 'Purchase' ? 'Past purchase:' : 'Policy:' }}</span>
          <span class="min-w-0 truncate">{{ label(source) }}</span>
        </li>
      }
    </ul>
  `,
})
export class SourceChips {
  readonly sources = input.required<StreamSource[]>();
  protected readonly label = sourceLabel;
  protected readonly path = sourcePath;
}
