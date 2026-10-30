import { NgTemplateOutlet } from '@angular/common';
import { afterNextRender, afterRenderEffect, Component, computed, effect, ElementRef, inject, Injector, OnInit, signal, viewChild } from '@angular/core';
import { disabled, form, FormField, maxLength } from '@angular/forms/signals';
import { AssistantStore, ChatMessage } from './assistant.store';
import { ConversationList } from './conversation-list';
import { MessageView, ViewMessage } from './message-view';

/** The server's limit for one question (`Message.MaxQuestionLength`). */
const MAX_QUESTION = 2000;
/** How close to the bottom (px) still counts as "at the latest". */
const BOTTOM_SLACK = 32;

const EXAMPLES = [
  'Do I need finance approval for R 150 000 of software for IT-200?',
  'What is the budget of IT-200?',
  'Search the policy for quotes for software',
];

/**
 * Screen 13: the policy assistant (`assistant.use`). Questions are answered from the company's policy and past purchases, streamed
 * with the tools used and the sources cited. Conversations on the left (a drawer on phones). Enter sends, Shift+Enter is a new
 * line; while an answer streams the input is off and Stop replaces Send. The view follows the answer until the user scrolls up
 * ("Jump to latest" brings it back).
 */
@Component({
  selector: 'app-assistant-page',
  imports: [NgTemplateOutlet, FormField, ConversationList, MessageView],
  template: `
    <section class="assistant-frame mx-auto flex max-w-6xl gap-6 px-4 py-4 sm:py-6">
      <aside class="hidden w-64 shrink-0 lg:block">
        <ng-container [ngTemplateOutlet]="list" />
      </aside>

      <div class="flex min-w-0 flex-1 flex-col">
        <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
          <button type="button" class="btn-secondary !px-3 lg:hidden" aria-haspopup="dialog" (click)="openDrawer()" data-testid="open-drawer">
            <span aria-hidden="true">☰</span> Conversations
          </button>
          <h1 class="min-w-0 truncate text-xl font-bold tracking-tight text-slate-900 sm:text-2xl">{{ store.current()?.title || 'Policy assistant' }}</h1>
          @if (store.info()?.demoModel) {
            <span class="badge-warn" data-testid="demo-model" title="No AI account is connected: a built-in model answers from the policy text.">Demo model</span>
          }
        </div>

        <div class="relative mt-3 flex min-h-0 flex-1 flex-col">
          <div
            #scroller
            class="min-h-0 flex-1 overflow-y-auto rounded-xl border border-slate-200 bg-white px-3 py-4 shadow-sm sm:px-5"
            data-testid="messages"
            (scroll)="onScroll()"
            aria-live="off"
          >
            @if (store.opening()) {
              <div class="space-y-3" role="status">
                <span class="sr-only">Loading the conversation</span>
                @for (row of [1, 2, 3]; track row) {
                  <div class="h-10 animate-pulse rounded-lg bg-slate-100" aria-hidden="true"></div>
                }
              </div>
            } @else if (store.messages().length === 0 && !store.streaming()) {
              <div class="mx-auto flex max-w-xl flex-col items-center py-8 text-center" data-testid="empty">
                <span aria-hidden="true" class="text-3xl">📘</span>
                <h2 class="mt-2 text-lg font-semibold text-slate-900">Ask about the procurement policy</h2>
                <p class="mt-1 text-sm text-slate-600">Approval thresholds, quotes, budgets, the status of a request or what was bought before. Every answer shows its sources.</p>
                <ul class="mt-5 flex w-full flex-col gap-2">
                  @for (example of examples; track example) {
                    <li>
                      <button type="button" class="w-full cursor-pointer rounded-lg border border-slate-200 bg-slate-50 px-4 py-2 text-left text-sm text-slate-700 hover:bg-indigo-50 disabled:cursor-not-allowed" [disabled]="store.inputLocked()" (click)="useExample(example)">
                        {{ example }}
                      </button>
                    </li>
                  }
                </ul>
              </div>
            } @else {
              <ol class="flex flex-col gap-5" aria-label="Messages">
                @for (m of store.messages(); track m.id; let i = $index) {
                  <li>
                    <app-message-view
                      [message]="m"
                      [actions]="m.role === 'Assistant' && i === lastAnswerIndex()"
                      [editable]="m.role === 'User' && i === lastQuestionIndex() && !store.streaming()"
                      [locked]="store.inputLocked()"
                      (regenerate)="regenerate()"
                      (edit)="startEdit(m)"
                    />
                  </li>
                }
                @if (streamingView(); as live) {
                  <li>
                    <app-message-view [message]="live" [streaming]="true" />
                  </li>
                }
              </ol>
            }
          </div>

          @if (!following() && (store.messages().length > 0 || store.streaming())) {
            <button
              type="button"
              class="btn-secondary absolute bottom-3 left-1/2 -translate-x-1/2 !rounded-full !py-1.5 shadow-md"
              (click)="jumpToLatest()"
              data-testid="jump-latest"
            >Jump to latest</button>
          }
        </div>

        <div class="sr-only" aria-live="polite">{{ liveStatus() }}</div>

        @if (store.notice(); as notice) {
          <div
            role="alert"
            data-testid="notice"
            class="mt-3 flex flex-wrap items-center justify-between gap-3 rounded-lg border px-4 py-3 text-sm"
            [class]="notice.kind === 'unavailable' ? 'border-red-200 bg-red-50 text-red-900' : 'border-amber-200 bg-amber-50 text-amber-900'"
          >
            <p class="min-w-0">
              <strong class="font-semibold">{{ notice.title }}</strong>
              @if (notice.detail) {
                {{ ' ' + notice.detail }}
              }
            </p>
            <div class="flex shrink-0 gap-2">
              @if (notice.retry) {
                <button type="button" class="btn-secondary !py-1" (click)="retry()" [disabled]="store.inputLocked()" data-testid="retry">Retry</button>
              }
              @if (notice.kind !== 'quota') {
                <button type="button" class="btn-ghost !px-2 !py-1" (click)="store.dismissNotice()" aria-label="Dismiss">✕</button>
              }
            </div>
          </div>
        }

        <form class="mt-3" (submit)="$event.preventDefault(); send()" novalidate>
          @if (editing()) {
            <p class="mb-2 flex items-center justify-between gap-2 rounded-lg bg-indigo-50 px-3 py-1.5 text-sm text-indigo-900">
              <span>Editing your last question. Its answer is replaced.</span>
              <button type="button" class="btn-ghost !px-2 !py-1 text-xs" (click)="cancelEdit()">Cancel</button>
            </p>
          }
          <div class="flex items-end gap-2">
            <label for="assistant-question" class="sr-only">Your question</label>
            <textarea
              #question
              id="assistant-question"
              rows="2"
              class="field-input max-h-40 min-h-[2.75rem] flex-1 resize-none"
              placeholder="Ask about the procurement policy…"
              [formField]="composer.question"
              (keydown)="onKeydown($event)"
              aria-describedby="assistant-footer"
              data-testid="question"
            ></textarea>
            @if (store.streaming()) {
              <button #stop type="button" class="btn-secondary h-11 shrink-0" (click)="store.stop()" data-testid="stop">
                <span aria-hidden="true" class="inline-block size-2.5 rounded-sm bg-slate-700"></span> Stop
              </button>
            } @else {
              <button type="submit" class="btn-primary h-11 shrink-0 disabled:cursor-not-allowed disabled:opacity-60" [disabled]="!canSend()" data-testid="send">
                {{ editing() ? 'Save' : 'Send' }}
              </button>
            }
          </div>
        </form>
        <p id="assistant-footer" class="mt-2 text-xs text-slate-600" data-testid="footer">
          Answers come only from your company's policy and past purchases.
          @if (store.quota(); as quota) {
            {{ quota.used }} of {{ quota.limit }} questions used today.
          }
        </p>
      </div>
    </section>

    <dialog
      #drawer
      class="drawer m-0 h-dvh max-h-dvh w-[min(20rem,85vw)] max-w-none border-r border-slate-200 bg-white p-4 shadow-xl backdrop:bg-slate-900/50"
      aria-label="Conversations"
      (click)="onDrawerClick($event)"
    >
      <div class="mb-3 flex items-center justify-between">
        <h2 class="text-base font-semibold text-slate-900">Conversations</h2>
        <button type="button" class="btn-ghost !px-2 !py-1" (click)="closeDrawer()" aria-label="Close conversations">✕</button>
      </div>
      <div class="h-[calc(100%-3rem)]">
        <ng-container [ngTemplateOutlet]="list" />
      </div>
    </dialog>

    <ng-template #list>
      <app-conversation-list
        [conversations]="store.conversations()"
        [currentId]="store.current()?.id ?? null"
        [disabled]="!!store.streaming()"
        [error]="store.listError()"
        (create)="newConversation()"
        (open)="open($event)"
        (remove)="remove($event)"
      />
    </ng-template>
  `,
  styles: `
    /* The chat fills the window below the shell header (taller below lg, where the nav has its own row). */
    .assistant-frame {
      height: calc(100dvh - 7.25rem);
      min-height: 26rem;
    }
    @media (min-width: 64rem) {
      .assistant-frame {
        height: calc(100dvh - 4.25rem);
      }
    }
    .drawer[open] {
      display: block;
    }
  `,
})
export class AssistantPage implements OnInit {
  protected readonly store = inject(AssistantStore);
  private readonly injector = inject(Injector);
  protected readonly examples = EXAMPLES;

  private readonly scroller = viewChild.required<ElementRef<HTMLElement>>('scroller');
  private readonly questionBox = viewChild.required<ElementRef<HTMLTextAreaElement>>('question');
  private readonly stopButton = viewChild<ElementRef<HTMLButtonElement>>('stop');
  private readonly drawer = viewChild.required<ElementRef<HTMLDialogElement>>('drawer');

  private readonly model = signal({ question: '' });
  protected readonly composer = form(this.model, (p) => {
    disabled(p.question, () => this.store.inputLocked());
    maxLength(p.question, MAX_QUESTION, { message: `Use at most ${MAX_QUESTION} characters` });
  });

  /** Editing the last question instead of asking a new one. */
  protected readonly editing = signal(false);
  /** The view follows new text until the user scrolls up. */
  protected readonly following = signal(true);
  private lastTop = 0;

  protected readonly canSend = computed(() => {
    const text = this.model().question.trim();
    return text !== '' && text.length <= MAX_QUESTION && !this.store.inputLocked();
  });

  protected readonly lastAnswerIndex = computed(() => lastIndexOf(this.store.messages(), 'Assistant', this.store.streaming() !== null));
  protected readonly lastQuestionIndex = computed(() => lastIndexOf(this.store.messages(), 'User', false));

  protected readonly streamingView = computed<ViewMessage | null>(() => {
    const live = this.store.streaming();
    return live ? { role: 'Assistant', text: live.text, status: null, toolSteps: live.tools, sources: live.sources } : null;
  });

  /** For screen readers: the start and the end of an answer, not every word. */
  protected readonly liveStatus = computed(() => {
    const live = this.store.streaming();
    if (live) {
      return live.status === 'sending' ? 'Sending your question.' : 'The assistant is answering.';
    }
    const last = this.store.messages().at(-1);
    return last?.role === 'Assistant' ? `Answer ${last.status === 'Complete' ? 'ready' : last.status.toLowerCase()}.` : '';
  });

  constructor() {
    // The input is disabled while an answer streams, which drops the keyboard focus: it goes to Stop, and back to the input when
    // the answer is done. Only when the focus is nowhere, so a user who moved on is not pulled back.
    let wasStreaming = false;
    effect(() => {
      const streaming = this.store.streaming() !== null;
      if (streaming !== wasStreaming) {
        wasStreaming = streaming;
        afterNextRender(
          () => {
            const active = document.activeElement;
            if (!active || active === document.body || !active.isConnected || (active as HTMLButtonElement).disabled) {
              (streaming ? this.stopButton()?.nativeElement : this.questionBox().nativeElement)?.focus();
            }
          },
          { injector: this.injector },
        );
      }
    });

    // Follow the answer: after each render that changed the messages or the streamed text, scroll to the bottom (unless the user scrolled up).
    afterRenderEffect({
      write: () => {
        this.store.messages();
        const live = this.store.streaming();
        live?.text;
        live?.tools;
        live?.sources;
        if (this.following()) {
          const el = this.scroller().nativeElement;
          el.scrollTop = el.scrollHeight;
          this.lastTop = el.scrollTop;
        }
      },
    });
  }

  ngOnInit(): void {
    void this.store.load();
  }

  protected onScroll(): void {
    const el = this.scroller().nativeElement;
    const top = el.scrollTop;
    if (el.scrollHeight - top - el.clientHeight <= BOTTOM_SLACK) {
      this.following.set(true);
    } else if (top < this.lastTop) {
      this.following.set(false);
    }
    this.lastTop = top;
  }

  protected jumpToLatest(): void {
    this.following.set(true);
    const el = this.scroller().nativeElement;
    el.scrollTop = el.scrollHeight;
    this.lastTop = el.scrollTop;
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Enter' || event.shiftKey || event.isComposing) {
      return;
    }
    event.preventDefault();
    void this.send();
  }

  protected async send(): Promise<void> {
    if (!this.canSend()) {
      return;
    }
    const text = this.model().question;
    this.following.set(true);
    const accepted = this.editing() ? await this.store.editLast(text) : await this.store.ask(text);
    if (accepted) {
      this.model.set({ question: '' });
      this.editing.set(false);
    }
  }

  protected regenerate(): void {
    this.following.set(true);
    void this.store.regenerate();
  }

  protected retry(): void {
    this.following.set(true);
    void this.store.retry();
  }

  protected startEdit(message: ChatMessage): void {
    this.editing.set(true);
    this.model.set({ question: message.text });
    this.questionBox().nativeElement.focus();
  }

  protected cancelEdit(): void {
    this.editing.set(false);
    this.model.set({ question: '' });
    this.questionBox().nativeElement.focus();
  }

  protected useExample(example: string): void {
    this.editing.set(false);
    this.model.set({ question: example });
    this.questionBox().nativeElement.focus();
  }

  protected newConversation(): void {
    this.closeDrawer();
    this.editing.set(false);
    this.store.newConversation();
    this.following.set(true);
    afterNextRender(() => this.questionBox().nativeElement.focus(), { injector: this.injector });
  }

  protected open(id: string): void {
    this.closeDrawer();
    this.editing.set(false);
    this.following.set(true);
    void this.store.open(id);
  }

  protected remove(id: string): void {
    void this.store.remove(id);
  }

  protected openDrawer(): void {
    this.drawer().nativeElement.showModal();
  }

  protected closeDrawer(): void {
    const dialog = this.drawer().nativeElement;
    if (dialog.open) {
      dialog.close();
    }
  }

  /** A click on the backdrop (the dialog element itself, outside its content box) closes the drawer. */
  protected onDrawerClick(event: MouseEvent): void {
    if (event.target === this.drawer().nativeElement) {
      const box = this.drawer().nativeElement.getBoundingClientRect();
      const inside = event.clientX >= box.left && event.clientX <= box.right && event.clientY >= box.top && event.clientY <= box.bottom;
      if (!inside) {
        this.closeDrawer();
      }
    }
  }
}

function lastIndexOf(messages: ChatMessage[], role: ChatMessage['role'], none: boolean): number {
  return none ? -1 : messages.map((m) => m.role).lastIndexOf(role);
}
