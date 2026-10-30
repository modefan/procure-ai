import { Component, computed, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MessageStatus } from '../../api/assistant/models/message-status';
import { StreamSource } from './answer-stream';
import { ToolStep } from './assistant.store';
import { renderMarkdown } from './markdown';
import { SourceChips } from './source-chips';
import { ToolSteps } from './tool-step';

/** What one bubble shows: a saved message, or the answer being written (`status` null while it streams). */
export interface ViewMessage {
  role: 'User' | 'Assistant';
  text: string;
  status: MessageStatus | null;
  toolSteps: ToolStep[];
  sources: StreamSource[];
  note?: string | null;
}

const COPIED_MS = 2000;

/** Runs `fn` on the next animation frame (a timer where there are no frames). */
const nextFrame = (fn: () => void): (() => void) => {
  if (typeof requestAnimationFrame === 'function') {
    const id = requestAnimationFrame(fn);
    return () => cancelAnimationFrame(id);
  }
  const id = setTimeout(fn, 16);
  return () => clearTimeout(id);
};

/**
 * One message. A question is plain text on the right. An answer shows its tool steps, its text as Markdown (sanitized, see
 * `renderMarkdown`; while streaming re-rendered at most once per animation frame, with a blinking cursor), Stopped / Blocked /
 * Failed when it did not complete, and its source chips. On the last turn: Copy and Regenerate on the answer, Edit on the question.
 */
@Component({
  selector: 'app-message-view',
  imports: [ToolSteps, SourceChips],
  template: `
    @let m = message();
    @if (m.role === 'User') {
      <div class="flex flex-col items-end gap-1" data-testid="question-bubble">
        <p class="max-w-[85%] rounded-2xl rounded-br-md bg-indigo-700 px-4 py-2.5 break-words whitespace-pre-wrap text-white sm:max-w-[75%]">{{ m.text }}</p>
        @if (editable()) {
          <button type="button" class="btn-ghost !px-2 !py-1 text-xs" (click)="edit.emit()" data-testid="edit">Edit</button>
        }
      </div>
    } @else {
      <article class="flex flex-col gap-2" [attr.aria-busy]="streaming() ? 'true' : null" data-testid="answer">
        @if (m.toolSteps.length > 0) {
          <app-tool-steps [steps]="m.toolSteps" />
        }
        @if (html()) {
          <div class="md-answer" [class.md-streaming]="streaming()" [innerHTML]="html()"></div>
        } @else if (streaming()) {
          <p class="text-slate-500"><span class="md-cursor" aria-hidden="true"></span><span class="sr-only">Writing the answer…</span></p>
        }
        @switch (m.status) {
          @case ('Stopped') {
            <p class="flex flex-wrap items-center gap-2 text-sm text-slate-600">
              <span class="badge-muted" data-testid="message-status">Stopped</span>
              @if (!m.text) {
                <span>Stopped before any answer was written.</span>
              }
            </p>
          }
          @case ('Blocked') {
            <div class="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900">
              <span class="badge-warn mr-2" data-testid="message-status">Blocked</span>
              {{ m.note || "I can't help with that. Ask about the procurement policy or past purchases." }}
            </div>
          }
          @case ('Failed') {
            <p class="flex flex-wrap items-center gap-2 text-sm text-slate-600">
              <span class="badge-error" data-testid="message-status">Not finished</span>
              @if (!m.text) {
                <span>The assistant could not answer this question.</span>
              }
            </p>
          }
        }
        @if (m.sources.length > 0 && m.status !== 'Blocked') {
          <app-source-chips [sources]="m.sources" />
        }
        @if (actions() && !streaming()) {
          <div class="flex flex-wrap gap-1">
            @if (m.text) {
              <button type="button" class="btn-ghost !px-2 !py-1 text-xs" (click)="copy()" data-testid="copy">
                {{ copied() ? 'Copied' : 'Copy' }}
              </button>
            }
            <button type="button" class="btn-ghost !px-2 !py-1 text-xs" (click)="regenerate.emit()" [disabled]="locked()" data-testid="regenerate">Regenerate</button>
          </div>
        }
      </article>
    }
  `,
})
export class MessageView {
  readonly message = input.required<ViewMessage>();
  /** True for the answer being written. */
  readonly streaming = input(false);
  /** Copy and Regenerate (an answer of the last turn). */
  readonly actions = input(false);
  /** Edit (the question of the last turn). */
  readonly editable = input(false);
  /** Regenerate is off while another answer streams or the quota is used up. */
  readonly locked = input(false);
  readonly regenerate = output<void>();
  readonly edit = output<void>();

  protected readonly copied = signal(false);
  /** The rendered Markdown. Set at once for a finished message; at most once per frame while streaming. */
  protected readonly html = signal('');
  private readonly text = computed(() => this.message().text);

  constructor() {
    let cancel: (() => void) | null = null;
    let copiedTimer: ReturnType<typeof setTimeout> | null = null;
    effect(() => {
      const text = this.text();
      if (!this.streaming()) {
        cancel?.();
        cancel = null;
        this.html.set(renderMarkdown(text));
        return;
      }
      if (!cancel) {
        cancel = nextFrame(() => {
          cancel = null;
          this.html.set(renderMarkdown(untracked(this.text)));
        });
      }
    });
    inject(DestroyRef).onDestroy(() => {
      cancel?.();
      if (copiedTimer) {
        clearTimeout(copiedTimer);
      }
    });
    this.copyDone = () => {
      this.copied.set(true);
      if (copiedTimer) {
        clearTimeout(copiedTimer);
      }
      copiedTimer = setTimeout(() => this.copied.set(false), COPIED_MS);
    };
  }

  private readonly copyDone: () => void;

  protected async copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.message().text);
      this.copyDone();
    } catch {
      // No clipboard (permission refused): nothing to copy to.
    }
  }
}
