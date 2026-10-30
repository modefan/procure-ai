import { AskQuestionRequest } from '../../api/assistant/models/ask-question-request';
import { AssistantInfo } from '../../api/assistant/models/assistant-info';
import { ConversationDto } from '../../api/assistant/models/conversation-dto';
import { ConversationListItem } from '../../api/assistant/models/conversation-list-item';
import { MessageDto } from '../../api/assistant/models/message-dto';
import { ProblemDetails } from '../../api/assistant/models/problem-details';
import { SseEvent, StreamProblem } from './answer-stream';

/** One answer stream of `FakeAnswerStream`: the test sends its events, ends it or refuses it. Abort rejects a waiting read like `fetch` does. */
export class FakeStream {
  private readonly queue: ({ event: SseEvent } | { end: true } | { error: unknown })[] = [];
  private wake: (() => void) | null = null;

  constructor(
    readonly conversationId: string,
    readonly body: AskQuestionRequest,
    readonly signal: AbortSignal,
  ) {
    signal.addEventListener('abort', () => this.wake?.());
  }

  emit(event: SseEvent): void {
    this.push({ event });
  }

  end(): void {
    this.push({ end: true });
  }

  /** The server refused before the stream (a plain ProblemDetails answer). */
  refuse(status: number, problem: ProblemDetails = {}): void {
    this.push({ error: new StreamProblem(status, { status, ...problem }) });
  }

  fail(error: unknown): void {
    this.push({ error });
  }

  private push(item: (typeof this.queue)[number]): void {
    this.queue.push(item);
    this.wake?.();
  }

  async *events(): AsyncGenerator<SseEvent> {
    while (true) {
      if (this.signal.aborted) {
        throw new DOMException('The operation was aborted.', 'AbortError');
      }
      const next = this.queue.shift();
      if (!next) {
        await new Promise<void>((resolve) => (this.wake = resolve));
        this.wake = null;
        continue;
      }
      if ('end' in next) {
        return;
      }
      if ('error' in next) {
        throw next.error;
      }
      yield next.event;
    }
  }
}

/** Stands in for `AnswerStream`: every call is recorded and returns a `FakeStream` the test drives. */
export class FakeAnswerStream {
  readonly streams: FakeStream[] = [];

  get last(): FakeStream {
    return this.streams[this.streams.length - 1];
  }

  streamAnswer(conversationId: string, body: AskQuestionRequest, signal: AbortSignal): AsyncIterable<SseEvent> {
    const stream = new FakeStream(conversationId, body, signal);
    this.streams.push(stream);
    return stream.events();
  }
}

export function fakeConversationsApi(overrides: { list?: ConversationListItem[]; conversation?: ConversationDto; quota?: { used: number; limit: number } } = {}) {
  return {
    listConversations: vi.fn().mockResolvedValue(overrides.list ?? []),
    createConversation: vi.fn().mockResolvedValue({ id: 'c-new' }),
    getConversation: vi.fn().mockResolvedValue(overrides.conversation ?? conversationDto()),
    deleteConversation: vi.fn().mockResolvedValue(undefined),
    getAssistantQuota: vi.fn().mockResolvedValue(overrides.quota ?? { used: 12, limit: 50 }),
  };
}

export function fakeInfoApi(info: Partial<AssistantInfo> = {}) {
  return {
    getAssistantInfo: vi.fn().mockResolvedValue({ provider: 'Fake', chatModel: 'fake-chat', demoModel: true, questionsPerDay: 50, ...info }),
  };
}

export function messageDto(overrides: Partial<MessageDto> = {}): MessageDto {
  return {
    id: 'm-1',
    role: 'User',
    text: 'Do I need finance approval for R 150 000 of software for IT-200?',
    status: 'Complete',
    createdAt: '2026-10-29T08:00:00Z',
    model: null,
    sources: [],
    toolSteps: [],
    usage: { input: 0, output: 0 },
    ...overrides,
  };
}

export function conversationDto(overrides: Partial<ConversationDto> = {}): ConversationDto {
  return {
    id: 'c-1',
    title: 'Finance approval for software',
    createdAt: '2026-10-29T08:00:00Z',
    updatedAt: '2026-10-29T08:01:00Z',
    messages: [],
    ...overrides,
  };
}

/** Lets pending promises and timers of the store run. */
export async function flush(times = 5): Promise<void> {
  for (let i = 0; i < times; i++) {
    await new Promise((resolve) => setTimeout(resolve));
  }
}
