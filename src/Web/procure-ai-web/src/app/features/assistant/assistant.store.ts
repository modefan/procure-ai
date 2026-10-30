import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withHooks, withMethods, withState } from '@ngrx/signals';
import { AskMode } from '../../api/assistant/models/ask-mode';
import { AssistantInfo } from '../../api/assistant/models/assistant-info';
import { ConversationListItem } from '../../api/assistant/models/conversation-list-item';
import { MessageDto } from '../../api/assistant/models/message-dto';
import { QuotaDto } from '../../api/assistant/models/quota-dto';
import { ToolStepDto } from '../../api/assistant/models/tool-step-dto';
import { AssistantInfoService } from '../../api/assistant/services/assistant-info.service';
import { ConversationsService } from '../../api/assistant/services/conversations.service';
import { idempotencyKey } from '../../core/http/idempotency';
import { Logger } from '../../core/logging/logger';
import { CacheRegistry } from '../../core/state/cache-registry';
import { withCache } from '../../core/state/with-cache';
import { errorMessage } from '../../core/state/with-request-status';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { AnswerStream, SseEvent, StreamProblem, StreamSource } from './answer-stream';

/** A tool the assistant ran for an answer: running (spinner), ok (✓) or failed (✗, the summary says why). */
export interface ToolStep {
  id: string;
  name: string;
  /** The arguments as JSON text. */
  arguments: string;
  status: 'running' | 'ok' | 'failed';
  summary: string | null;
  durationMs: number | null;
}

/** A message on screen: a saved `MessageDto`, or one this store made from a stream (its sources then have no chunk id). */
export interface ChatMessage extends Omit<MessageDto, 'toolSteps' | 'sources'> {
  toolSteps: ToolStep[];
  sources: StreamSource[];
  /** Why a blocked answer was blocked (only known right after the stream). */
  note?: string | null;
}

/** The answer being written. `sending` until the server took the question, then `streaming`. */
export interface StreamingAnswer {
  status: 'sending' | 'streaming';
  text: string;
  tools: ToolStep[];
  sources: StreamSource[];
  messageId?: string;
}

/** What the page shows above the input: the quota, "still answering", the assistant being down (with Retry), or a refusal. */
export interface Notice {
  kind: 'quota' | 'busy' | 'unavailable' | 'refused';
  title: string;
  detail: string | null;
  retry: boolean;
}

interface AssistantState {
  conversations: ConversationListItem[];
  current: { id: string; title: string } | null;
  /** Messages per conversation id (in memory only). `messages` is the current conversation's. */
  threads: Record<string, ChatMessage[]>;
  streaming: StreamingAnswer | null;
  quota: QuotaDto | null;
  info: AssistantInfo | null;
  notice: Notice | null;
  /** True while the open conversation is being read and nothing of it is shown yet. */
  opening: boolean;
  listError: string | null;
}

export const UNAVAILABLE = 'The assistant is unavailable right now.';
export const BUSY = 'Still answering the previous question.';
const TITLE_LENGTH = 80;

const toolStepOf = (dto: ToolStepDto): ToolStep => ({
  id: dto.id,
  name: dto.name,
  arguments: dto.arguments,
  status: dto.ok ? 'ok' : 'failed',
  summary: dto.summary,
  durationMs: dto.durationMs,
});

const messageOf = (dto: MessageDto): ChatMessage => ({ ...dto, toolSteps: dto.toolSteps.map(toolStepOf) });

const isAbort = (error: unknown) => error instanceof DOMException && error.name === 'AbortError';

/**
 * The policy assistant (screen 13): the caller's conversations, the open one's messages, and the answer being streamed.
 *
 * `ask`, `regenerate` and `editLast` post the question (`AnswerStream`) and resolve true once the server took it (the first event)
 * or false when it refused before the stream (429 quota, 409 still answering, 422 ...); the answer keeps streaming into
 * `streaming` and becomes a message on `done`. One answer at a time. `stop()` aborts the request: the partial text stays, marked
 * Stopped (the server saves it the same way), and the next question can go at once. An `error` event (or a broken stream) leaves the
 * answer Failed with "The assistant is unavailable right now." and Retry; a guard's 422 makes it Blocked with the reason. Writes
 * wait for the server; nothing is optimistic except showing the question while it is sent.
 *
 * The list, the quota and the info are stale-while-revalidate (`withCache`), in memory only; logout clears everything and aborts
 * a running answer.
 */
export const AssistantStore = signalStore(
  { providedIn: 'root' },
  withState<AssistantState>({
    conversations: [],
    current: null,
    threads: {},
    streaming: null,
    quota: null,
    info: null,
    notice: null,
    opening: false,
    listError: null,
  }),
  withCache(),
  withComputed((store) => {
    const quotaReached = computed(() => {
      const quota = store.quota();
      return quota !== null && quota.used >= quota.limit;
    });
    return {
      messages: computed<ChatMessage[]>(() => {
        const current = store.current();
        return current ? (store.threads()[current.id] ?? []) : [];
      }),
      quotaReached,
      /** The input and Send are off while an answer streams and once today's questions are used up. */
      inputLocked: computed(() => store.streaming() !== null || quotaReached()),
    };
  }),
  withMethods((store) => {
    const api = inject(ConversationsService);
    const infoApi = inject(AssistantInfoService);
    const answers = inject(AnswerStream);
    const confirm = inject(ConfirmDialog);
    const log = inject(Logger).withSource('AssistantStore');
    let controller: AbortController | null = null;
    let openCall = 0;

    const setThread = (id: string, update: (messages: ChatMessage[]) => ChatMessage[]) =>
      patchState(store, (s) => ({ threads: { ...s.threads, [id]: update(s.threads[id] ?? []) } }));

    const patchStreaming = (update: (s: StreamingAnswer) => Partial<StreamingAnswer>) =>
      patchState(store, (s) => (s.streaming ? { streaming: { ...s.streaming, ...update(s.streaming) } } : {}));

    const loadList = () =>
      store.cacheLoad('conversations', async () => {
        const epoch = store.cacheEpoch();
        try {
          const conversations = await api.listConversations();
          if (epoch === store.cacheEpoch()) {
            patchState(store, { conversations, listError: null });
          }
        } catch (error) {
          if (epoch === store.cacheEpoch()) {
            log.warn('Loading the conversations failed', error);
            patchState(store, { listError: errorMessage(error, 'Your conversations could not be loaded.') });
          }
          throw error;
        }
      });

    const loadQuota = () =>
      store.cacheLoad('quota', async () => {
        const epoch = store.cacheEpoch();
        const quota = await api.getAssistantQuota();
        if (epoch === store.cacheEpoch() && store.streaming() === null) {
          patchState(store, { quota });
        }
      });

    const loadInfo = () =>
      store.cacheLoad('info', async () => {
        const epoch = store.cacheEpoch();
        const info = await infoApi.getAssistantInfo();
        if (epoch === store.cacheEpoch()) {
          patchState(store, { info });
        }
      });

    const loadConversation = (id: string) =>
      store.cacheLoad(`conversation:${id}`, async () => {
        const epoch = store.cacheEpoch();
        const conversation = await api.getConversation({ id });
        // An answer streaming into this conversation is newer than what the server had.
        if (epoch !== store.cacheEpoch() || (store.streaming() !== null && store.current()?.id === id)) {
          return;
        }
        setThread(id, () => conversation.messages.map(messageOf));
        patchState(store, (s) => ({ current: s.current?.id === id ? { id, title: conversation.title } : s.current }));
      });

    /** The streamed answer as a message, with its final status. Tools still running did not finish. */
    const finish = (conversationId: string, status: ChatMessage['status'], extra: Partial<ChatMessage> = {}) => {
      const answer = store.streaming();
      if (!answer) {
        return;
      }
      const message: ChatMessage = {
        id: answer.messageId ?? `local-${crypto.randomUUID()}`,
        role: 'Assistant',
        text: answer.text,
        status,
        createdAt: new Date().toISOString(),
        model: null,
        sources: answer.sources,
        toolSteps: answer.tools.map((t) => (t.status === 'running' ? { ...t, status: 'failed' as const, summary: t.summary ?? 'Not finished.' } : t)),
        usage: { input: 0, output: 0 },
        ...extra,
      };
      setThread(conversationId, (messages) => [...messages, message]);
      patchState(store, { streaming: null });
    };

    const apply = (event: SseEvent, conversationId: string): 'more' | 'done' => {
      switch (event.type) {
        case 'sources':
          patchStreaming(() => ({ sources: event.data }));
          return 'more';
        case 'delta':
          patchStreaming((s) => ({ text: s.text + event.data.text }));
          return 'more';
        case 'replace':
          patchStreaming(() => ({ text: event.data.text }));
          return 'more';
        case 'tool_call':
          patchStreaming((s) => ({
            tools: [
              ...s.tools,
              { id: event.data.id, name: event.data.name, arguments: JSON.stringify(event.data.arguments ?? {}), status: 'running', summary: null, durationMs: null },
            ],
          }));
          return 'more';
        case 'tool_result':
          patchStreaming((s) => ({
            tools: s.tools.map((t) =>
              t.id === event.data.id ? { ...t, status: event.data.ok ? 'ok' : 'failed', summary: event.data.summary, durationMs: event.data.durationMs } : t,
            ),
          }));
          return 'more';
        case 'done':
          patchStreaming(() => ({ messageId: event.data.messageId }));
          finish(conversationId, 'Complete', { model: event.data.model, usage: event.data.usage });
          return 'done';
        case 'error':
          if (event.data.status === 422) {
            const reason = [event.data.title, event.data.detail].filter((t) => !!t).map((t) => t!.replace(/[.\s]*$/, '.')).join(' ');
            finish(conversationId, 'Blocked', { note: reason || null });
          } else {
            log.warn('The answer stream sent an error', { status: event.data.status });
            finish(conversationId, 'Failed');
            patchState(store, { notice: { kind: 'unavailable', title: UNAVAILABLE, detail: event.data.detail ?? null, retry: true } });
          }
          return 'done';
      }
    };

    /** Before the stream: the server said no. */
    const noticeOf = (error: unknown): Notice => {
      if (error instanceof StreamProblem) {
        const detail = error.problem.detail ?? null;
        switch (error.status) {
          case 429:
            return { kind: 'quota', title: error.problem.title ?? "You've used all your questions for today.", detail, retry: false };
          case 409:
            return { kind: 'busy', title: BUSY, detail: null, retry: false };
          case 400:
          case 404:
          case 422:
            return { kind: 'refused', title: error.problem.title ?? 'The question could not be asked.', detail, retry: false };
        }
      }
      return { kind: 'unavailable', title: UNAVAILABLE, detail: 'Try again in a moment.', retry: false };
    };

    /**
     * Posts a question and streams its answer. `local` rewrites the thread for the mode (shown while sending, undone on a refusal).
     * Resolves when the server took the question (true) or refused it (false); the stream then goes on.
     */
    const send = (conversationId: string, text: string, mode: Exclude<AskMode, null>, local: (messages: ChatMessage[]) => ChatMessage[]): Promise<boolean> => {
      const epoch = store.cacheEpoch();
      const before = store.threads()[conversationId] ?? [];
      const mine = new AbortController();
      controller = mine;
      setThread(conversationId, local);
      patchState(store, { streaming: { status: 'sending', text: '', tools: [], sources: [] }, notice: null });

      let accept!: (accepted: boolean) => void;
      const accepted = new Promise<boolean>((resolve) => (accept = resolve));
      let started = false;
      const start = () => {
        if (!started) {
          started = true;
          patchStreaming(() => ({ status: 'streaming' }));
          patchState(store, (s) => ({ quota: s.quota ? { ...s.quota, used: s.quota.used + 1 } : s.quota }));
          accept(true);
        }
      };

      void (async () => {
        let ended: 'more' | 'done' = 'more';
        try {
          for await (const event of answers.streamAnswer(conversationId, { text, mode }, mine.signal)) {
            if (epoch !== store.cacheEpoch()) {
              mine.abort();
              return;
            }
            start();
            ended = apply(event, conversationId);
            if (ended === 'done') {
              break;
            }
          }
          if (ended === 'more' && epoch === store.cacheEpoch()) {
            // The stream ended without done or error: what came is kept, but it is not a whole answer.
            start();
            finish(conversationId, 'Failed');
            patchState(store, { notice: { kind: 'unavailable', title: UNAVAILABLE, detail: 'The answer broke off.', retry: true } });
          }
        } catch (error) {
          if (epoch !== store.cacheEpoch()) {
            return;
          }
          if (isAbort(error) && mine.signal.aborted) {
            // Stopped by the user. The server saves the same partial answer as Stopped.
            start();
            finish(conversationId, 'Stopped');
          } else if (!started) {
            setThread(conversationId, () => before);
            const notice = noticeOf(error);
            if (notice.kind === 'unavailable') {
              log.warn('Asking the assistant failed', error);
            }
            patchState(store, (s) => ({
              streaming: null,
              notice,
              quota: notice.kind === 'quota' && s.quota ? { ...s.quota, used: Math.max(s.quota.used, s.quota.limit) } : s.quota,
            }));
            accept(false);
          } else {
            log.warn('The answer stream broke off', error);
            finish(conversationId, 'Failed');
            patchState(store, { notice: { kind: 'unavailable', title: UNAVAILABLE, detail: 'Try again in a moment.', retry: true } });
          }
        } finally {
          if (controller === mine) {
            controller = null;
          }
          accept(started);
          // A refused question changed nothing on the server; an answered (or stopped) one changed the list order and the count.
          if (started && epoch === store.cacheEpoch()) {
            store.invalidate('conversations');
            store.invalidate('quota');
          }
        }
      })();
      return accepted;
    };

    const userMessage = (text: string): ChatMessage => ({
      id: `local-${crypto.randomUUID()}`,
      role: 'User',
      text,
      status: 'Complete',
      createdAt: new Date().toISOString(),
      model: null,
      sources: [],
      toolSteps: [],
      usage: { input: 0, output: 0 },
    });

    /** Everything up to and including the last question; null when there is none. */
    const upToLastQuestion = (messages: ChatMessage[]): ChatMessage[] | null => {
      const index = messages.map((m) => m.role).lastIndexOf('User');
      return index < 0 ? null : messages.slice(0, index + 1);
    };

    const stopRunning = () => {
      controller?.abort();
    };

    const regenerate = async (): Promise<boolean> => {
      const current = store.current();
      const kept = upToLastQuestion(store.messages());
      if (!current || !kept || store.inputLocked()) {
        return false;
      }
      return send(current.id, kept[kept.length - 1].text, 'Regenerate', () => kept);
    };

    return {
      /** The list, the quota and the info. Does nothing while they are fresh. */
      async load(): Promise<void> {
        await Promise.allSettled([loadList(), loadQuota(), loadInfo()]);
      },

      async ask(text: string): Promise<boolean> {
        const question = text.trim();
        if (question === '' || store.inputLocked()) {
          return false;
        }
        let current = store.current();
        if (!current) {
          const epoch = store.cacheEpoch();
          patchState(store, { streaming: { status: 'sending', text: '', tools: [], sources: [] }, notice: null });
          try {
            const created = await api.createConversation({ 'Idempotency-Key': idempotencyKey() });
            if (epoch !== store.cacheEpoch()) {
              return false;
            }
            current = { id: created.id, title: question.slice(0, TITLE_LENGTH) };
            const now = new Date().toISOString();
            patchState(store, (s) => ({
              current,
              threads: { ...s.threads, [created.id]: [] },
              conversations: [{ id: created.id, title: current!.title, createdAt: now, updatedAt: now }, ...s.conversations],
            }));
          } catch (error) {
            if (epoch === store.cacheEpoch()) {
              log.warn('Starting a conversation failed', error);
              patchState(store, { streaming: null, notice: { kind: 'unavailable', title: UNAVAILABLE, detail: errorMessage(error, 'Try again in a moment.'), retry: false } });
            }
            return false;
          }
          patchState(store, { streaming: null });
        }
        return send(current.id, question, 'New', (messages) => [...messages, userMessage(question)]);
      },

      /** Answers the last question again. */
      regenerate,

      /** Retry after "unavailable": the same question again. */
      retry: regenerate,

      /** Replaces the last question (and its answer) with this text. */
      async editLast(text: string): Promise<boolean> {
        const question = text.trim();
        const current = store.current();
        const kept = upToLastQuestion(store.messages());
        if (!current || !kept || question === '' || store.inputLocked()) {
          return false;
        }
        return send(current.id, question, 'Edit', () => [...kept.slice(0, -1), userMessage(question)]);
      },

      /** Stops the answer being written; what came so far stays, marked Stopped. */
      stop(): void {
        stopRunning();
      },

      dismissNotice(): void {
        patchState(store, (s) => ({ notice: s.notice?.kind === 'quota' ? s.notice : null }));
      },

      /** An empty screen for a new conversation; it is created with the first question. */
      newConversation(): void {
        stopRunning();
        openCall++;
        patchState(store, (s) => ({ current: null, notice: s.notice?.kind === 'quota' ? s.notice : null, opening: false }));
      },

      /** Shows a conversation: what is in memory at once, the server's version when it arrives. */
      async open(id: string): Promise<void> {
        if (store.current()?.id === id && store.threads()[id]) {
          await loadConversation(id).catch(() => undefined);
          return;
        }
        stopRunning();
        const call = ++openCall;
        const item = store.conversations().find((c) => c.id === id);
        patchState(store, (s) => ({
          current: { id, title: item?.title ?? '' },
          notice: s.notice?.kind === 'quota' ? s.notice : null,
          opening: !s.threads[id],
        }));
        try {
          await loadConversation(id);
        } catch (error) {
          if (call === openCall) {
            log.warn('Opening the conversation failed', error);
            patchState(store, { notice: { kind: 'refused', title: errorMessage(error, 'The conversation could not be opened.'), detail: null, retry: false } });
          }
        } finally {
          if (call === openCall) {
            patchState(store, { opening: false });
          }
        }
      },

      /** Deletes a conversation after asking. Resolves true when it is gone. */
      async remove(id: string): Promise<boolean> {
        const item = store.conversations().find((c) => c.id === id);
        const confirmed = await confirm.ask({
          title: 'Delete this conversation?',
          body: `"${item?.title || 'New conversation'}" and its answers are deleted for good.`,
          confirm: 'Delete',
          cancel: 'Keep it',
        });
        if (!confirmed) {
          return false;
        }
        if (store.current()?.id === id) {
          stopRunning();
        }
        const epoch = store.cacheEpoch();
        try {
          await api.deleteConversation({ id });
        } catch (error) {
          if (epoch === store.cacheEpoch()) {
            log.warn('Deleting the conversation failed', error);
            patchState(store, { notice: { kind: 'refused', title: errorMessage(error, 'The conversation could not be deleted.'), detail: null, retry: false } });
          }
          return false;
        }
        if (epoch !== store.cacheEpoch()) {
          return false;
        }
        patchState(store, (s) => {
          const threads = { ...s.threads };
          delete threads[id];
          return {
            conversations: s.conversations.filter((c) => c.id !== id),
            threads,
            current: s.current?.id === id ? null : s.current,
          };
        });
        return true;
      },
    };
  }),
  withHooks((store) => {
    let deregister: (() => void) | null = null;
    return {
      onInit() {
        // Logout clears every store (withCache resets this one); this also aborts an answer still streaming.
        deregister = inject(CacheRegistry).register({ clearCache: () => store.stop() });
      },
      onDestroy() {
        deregister?.();
        store.stop();
      },
    };
  }),
);

export type AssistantStore = InstanceType<typeof AssistantStore>;
