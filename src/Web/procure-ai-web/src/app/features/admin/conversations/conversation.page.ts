import { DatePipe } from '@angular/common';
import { Component, inject, OnInit } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AdminMessageDto } from '../../../api/assistant/models/admin-message-dto';
import { SourceChips } from '../../assistant/source-chips';
import { ToolStep } from '../../assistant/assistant.store';
import { ToolSteps } from '../../assistant/tool-step';
import { ConversationsStore } from './conversations.store';

/** The saved tool steps as the chat shows them (a saved step is finished: it worked or it failed). */
export function toolStepsOf(message: AdminMessageDto): ToolStep[] {
  return message.toolSteps.map((s) => ({
    id: s.id,
    name: s.name,
    arguments: s.arguments,
    status: s.ok ? 'ok' : 'failed',
    summary: s.summary,
    durationMs: s.durationMs,
  }));
}

/** One assistant conversation for an admin: every message with its tool steps, sources, tokens and guard flags. Read-only. */
@Component({
  selector: 'app-conversation-page',
  imports: [DatePipe, RouterLink, SourceChips, ToolSteps],
  template: `
    <section class="mx-auto max-w-4xl px-4 py-8">
      <a routerLink="/app/admin/conversations" class="text-sm text-indigo-700 hover:underline">&lsaquo; All conversations</a>

      @if (store.error(); as message) {
        <div role="alert" class="mt-6 rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-900">{{ message }}</div>
      }

      @if (store.isLoading() && !store.detail()) {
        <div class="card mt-6 space-y-4" role="status" aria-live="polite">
          <span class="sr-only">Loading the conversation</span>
          @for (row of skeletonRows; track row) {
            <div class="h-6 animate-pulse rounded bg-slate-200" aria-hidden="true"></div>
          }
        </div>
      } @else if (store.detail(); as conversation) {
        <h1 class="mt-2 text-2xl font-bold tracking-tight break-words text-slate-900">{{ conversation.title }}</h1>
        <p class="mt-1 text-sm text-slate-600">
          User <span class="font-medium text-slate-800">{{ store.names().get(conversation.userId) ?? conversation.userId }}</span> · started {{ conversation.createdAt | date: 'd MMM y, HH:mm' }} ·
          {{ conversation.messages.length }} messages
          @if (store.blockedCount() > 0) {
            · <span class="badge-error">{{ store.blockedCount() }} blocked</span>
          }
        </p>

        <ol class="mt-6 space-y-4">
          @for (message of conversation.messages; track message.id) {
            <li data-testid="admin-message" [attr.data-role]="message.role" class="rounded-xl border bg-white p-4 shadow-sm" [class]="message.role === 'User' ? 'border-indigo-200' : 'border-slate-200'">
              <div class="flex flex-wrap items-center justify-between gap-2 text-xs text-slate-600">
                <span class="font-semibold tracking-wide uppercase">{{ message.role === 'User' ? 'Question' : 'Answer' }}</span>
                <span class="whitespace-nowrap">{{ message.createdAt | date: 'HH:mm:ss' }}</span>
              </div>
              @if (message.toolSteps.length > 0) {
                <div class="mt-3"><app-tool-steps [steps]="stepsOf(message)" /></div>
              }
              <p class="mt-3 break-words whitespace-pre-wrap text-slate-900">{{ message.text }}</p>
              @if (message.sources.length > 0) {
                <div class="mt-3"><app-source-chips [sources]="message.sources" /></div>
              }
              <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-xs text-slate-600">
                @if (message.status !== 'Complete') {
                  <span [class]="message.status === 'Blocked' || message.status === 'Failed' ? 'badge-error' : 'badge-muted'">{{ message.status }}</span>
                }
                @if (message.guardFlags.length > 0) {
                  <span class="flex flex-wrap items-center gap-1.5" data-testid="guard-flags">
                    Guard:
                    @for (flag of message.guardFlags; track flag) {
                      <span class="badge-warn">{{ flag }}</span>
                    }
                  </span>
                }
                @if (message.role === 'Assistant') {
                  <span class="whitespace-nowrap">{{ message.usage.input }} in · {{ message.usage.output }} out</span>
                  @if (message.model) {
                    <span class="whitespace-nowrap">{{ message.model }}</span>
                  }
                  @if (message.latencyMs !== null) {
                    <span class="whitespace-nowrap">{{ message.latencyMs }} ms</span>
                  }
                  @if (message.timeToFirstTokenMs !== null) {
                    <span class="whitespace-nowrap">first token {{ message.timeToFirstTokenMs }} ms</span>
                  }
                }
              </div>
            </li>
          }
        </ol>
      }
    </section>
  `,
})
export class ConversationPage implements OnInit {
  protected readonly store = inject(ConversationsStore);
  private readonly route = inject(ActivatedRoute);
  protected readonly skeletonRows = [1, 2, 3];
  protected readonly stepsOf = toolStepsOf;

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id');
    void this.store.loadUsers();
    if (id) {
      void this.store.loadDetail(id);
    }
  }
}
