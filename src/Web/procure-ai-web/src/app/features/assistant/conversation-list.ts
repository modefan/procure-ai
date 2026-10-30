import { Component, input, output } from '@angular/core';
import { ConversationListItem } from '../../api/assistant/models/conversation-list-item';

/** The caller's conversations (newest first) with "New conversation", open and delete. Beside the chat on wide screens, in a drawer on phones. */
@Component({
  selector: 'app-conversation-list',
  template: `
    <nav aria-label="Conversations" class="flex h-full min-h-0 flex-col gap-3">
      <button type="button" class="btn-secondary w-full" (click)="create.emit()" [disabled]="disabled()" data-testid="new-conversation">
        <span aria-hidden="true">＋</span> New conversation
      </button>
      @if (error()) {
        <p role="alert" class="text-sm text-red-800">{{ error() }}</p>
      }
      @if (conversations().length === 0 && !error()) {
        <p class="px-1 text-sm text-slate-500">No conversations yet. Your questions are kept here.</p>
      } @else {
        <ul class="-mx-1 min-h-0 flex-1 space-y-0.5 overflow-y-auto px-1 py-0.5" data-testid="conversations">
          @for (c of conversations(); track c.id) {
            <li class="group flex items-center gap-1 rounded-lg" [class.bg-indigo-50]="c.id === currentId()">
              <button
                type="button"
                class="min-w-0 flex-1 cursor-pointer truncate rounded-lg px-3 py-2 text-left text-sm text-slate-700 hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-60"
                [class.font-semibold]="c.id === currentId()"
                [class.text-indigo-900]="c.id === currentId()"
                [attr.aria-current]="c.id === currentId() ? 'page' : null"
                [disabled]="disabled()"
                [title]="c.title || 'New conversation'"
                (click)="open.emit(c.id)"
              >
                {{ c.title || 'New conversation' }}
              </button>
              <button
                type="button"
                class="shrink-0 cursor-pointer rounded-md px-2 py-1.5 text-slate-500 hover:bg-red-50 hover:text-red-800 disabled:cursor-not-allowed disabled:opacity-60"
                [attr.aria-label]="'Delete ' + (c.title || 'new conversation')"
                [disabled]="disabled()"
                (click)="remove.emit(c.id)"
              >
                <span aria-hidden="true">🗑</span>
              </button>
            </li>
          }
        </ul>
      }
    </nav>
  `,
})
export class ConversationList {
  readonly conversations = input.required<ConversationListItem[]>();
  readonly currentId = input<string | null>(null);
  /** While an answer streams, switching conversation would stop it: the list waits. */
  readonly disabled = input(false);
  readonly error = input<string | null>(null);
  readonly create = output<void>();
  readonly open = output<string>();
  readonly remove = output<string>();
}
