import { HttpXsrfTokenExtractor } from '@angular/common/http';
import { inject, InjectionToken, Service } from '@angular/core';
import { Router } from '@angular/router';
import { AskQuestionRequest } from '../../api/assistant/models/ask-question-request';
import { ProblemDetails } from '../../api/assistant/models/problem-details';
import { SourceDto } from '../../api/assistant/models/source-dto';
import { ConversationsService } from '../../api/assistant/services/conversations.service';
import { AuthStore } from '../../core/auth/auth.store';
import { SessionExpired } from '../../core/auth/session-expired';
import { AppConfigService } from '../../core/config/app-config';
import { CORRELATION_HEADER } from '../../core/http/correlation.interceptor';
import { XSRF_HEADER_NAME } from '../../core/http/xsrf';
import { Logger } from '../../core/logging/logger';

/** A source as the stream sends it: the saved `SourceDto` without the chunk id. */
export type StreamSource = Omit<SourceDto, 'chunkId'>;

/**
 * The events of an answer stream, exactly as the Assistant API sends them (`SseEvent` in ProcureAi.Assistant.Application.Chat):
 * camelCase JSON, enum values PascalCase (`"type":"Policy"`). A second `sources` event carries the full list (the client replaces
 * its list). `error` is a ProblemDetails-like failure and the last event; after `done` the server has saved the answer.
 */
export type SseEvent =
  | { type: 'tool_call'; data: { id: string; name: string; arguments: unknown } }
  | { type: 'tool_result'; data: { id: string; ok: boolean; summary: string; durationMs: number } }
  | { type: 'sources'; data: StreamSource[] }
  | { type: 'delta'; data: { text: string } }
  | { type: 'replace'; data: { text: string } }
  | { type: 'done'; data: { messageId: string; usage: { input: number; output: number }; model: string } }
  | { type: 'error'; data: { title: string; detail: string | null; status: number } };

const EVENT_NAMES: ReadonlySet<string> = new Set<SseEvent['type']>(['tool_call', 'tool_result', 'sources', 'delta', 'replace', 'done', 'error']);

/** The server refused the question before the stream started (400, 404, 409, 422, 429 ...): its status and ProblemDetails. */
export class StreamProblem extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails,
  ) {
    super(problem.title ?? `The question was refused (${status}).`);
    this.name = 'StreamProblem';
  }
}

/** `fetch`, replaceable in tests. */
export const FETCH = new InjectionToken<typeof fetch>('FETCH', { factory: () => (input, init) => fetch(input, init) });

/**
 * Reads a `text/event-stream` body: `event:` and `data:` lines (several `data:` lines are joined with a newline), an event per
 * blank line, `:` comment lines skipped, LF or CRLF. Chunks may end anywhere, even inside a line or a UTF-8 character. Unknown
 * event names and data that is not JSON are skipped. Stopping the iteration (break) or aborting `signal` cancels the body; an
 * abort rejects the iteration with the signal's reason (an `AbortError`).
 */
export async function* parseSse(body: ReadableStream<Uint8Array>, signal?: AbortSignal): AsyncGenerator<SseEvent> {
  const reader = body.getReader();
  const onAbort = () => void reader.cancel(signal?.reason).catch(() => undefined);
  signal?.addEventListener('abort', onAbort, { once: true });
  const decoder = new TextDecoder();
  let buffer = '';
  let name = '';
  let data: string[] = [];

  function* line(text: string): Generator<SseEvent> {
    if (text === '') {
      const event = toEvent(name, data);
      name = '';
      data = [];
      if (event) {
        yield event;
      }
      return;
    }
    if (text.startsWith(':')) {
      return;
    }
    const colon = text.indexOf(':');
    const field = colon < 0 ? text : text.slice(0, colon);
    let value = colon < 0 ? '' : text.slice(colon + 1);
    if (value.startsWith(' ')) {
      value = value.slice(1);
    }
    if (field === 'event') {
      name = value;
    } else if (field === 'data') {
      data.push(value);
    }
  }

  let finished = false;
  try {
    while (true) {
      signal?.throwIfAborted();
      const { done, value } = await reader.read();
      signal?.throwIfAborted();
      buffer += done ? decoder.decode() : decoder.decode(value, { stream: true });
      let end: number;
      while ((end = buffer.search(/\r\n|\r|\n/)) >= 0) {
        const text = buffer.slice(0, end);
        // A CR at the very end of a chunk may be the first half of a CRLF: wait for the next chunk.
        if (buffer[end] === '\r' && end === buffer.length - 1 && !done) {
          break;
        }
        buffer = buffer.slice(end + (buffer.startsWith('\r\n', end) ? 2 : 1));
        yield* line(text);
      }
      if (done) {
        if (buffer !== '') {
          yield* line(buffer);
          buffer = '';
        }
        yield* line('');
        finished = true;
        return;
      }
    }
  } finally {
    signal?.removeEventListener('abort', onAbort);
    if (!finished) {
      // The caller stopped reading (break or abort): tell the server, so the model stops too.
      await reader.cancel().catch(() => undefined);
    }
    reader.releaseLock();
  }
}

function toEvent(name: string, data: string[]): SseEvent | null {
  if (!EVENT_NAMES.has(name) || data.length === 0) {
    return null;
  }
  try {
    return { type: name, data: JSON.parse(data.join('\n')) } as SseEvent;
  } catch {
    return null;
  }
}

/**
 * The one hand-written API call (allowed: `EventSource` can't POST): asks a question and yields the answer's events as they
 * arrive. It does what the HttpClient interceptors do for every other call: the cookies (`credentials`), the XSRF header, a
 * correlation id, and a 401 opens the session-expired dialog. A non-2xx answer before the stream is a `StreamProblem`; the
 * `signal` aborts the request and the read (the iteration then rejects with an `AbortError`).
 */
@Service()
export class AnswerStream {
  private readonly fetch = inject(FETCH);
  private readonly config = inject(AppConfigService);
  private readonly xsrf = inject(HttpXsrfTokenExtractor);
  private readonly sessionExpired = inject(SessionExpired);
  private readonly auth = inject(AuthStore);
  private readonly router = inject(Router);
  private readonly log = inject(Logger).withSource('AnswerStream');

  async *streamAnswer(conversationId: string, body: AskQuestionRequest, signal: AbortSignal): AsyncGenerator<SseEvent> {
    const base = this.config.config().apiBaseUrl;
    const path = ConversationsService.AskQuestionPath.replace('{id}', encodeURIComponent(conversationId));
    const correlationId = crypto.randomUUID();
    const headers: Record<string, string> = {
      Accept: 'text/event-stream',
      'Content-Type': 'application/json',
      [CORRELATION_HEADER]: correlationId,
    };
    const token = this.xsrf.getToken();
    if (token) {
      headers[XSRF_HEADER_NAME] = token;
    }

    const response = await this.fetch(base + path, {
      method: 'POST',
      headers,
      body: JSON.stringify(body),
      credentials: base === '' ? 'same-origin' : 'include',
      cache: 'no-store',
      signal,
    });

    if (!response.ok || !response.body) {
      const problem = await problemOf(response);
      const details = { method: 'POST', url: ConversationsService.AskQuestionPath, status: response.status, correlationId };
      if (response.status >= 500) {
        this.log.error('Asking the assistant failed', details);
      } else {
        this.log.warn(`Asking the assistant returned ${response.status}`, details);
      }
      if (response.status === 401 && this.auth.isLoggedIn()) {
        this.sessionExpired.show(this.router.url);
      }
      throw new StreamProblem(response.status, problem);
    }

    yield* parseSse(response.body, signal);
  }
}

async function problemOf(response: Response): Promise<ProblemDetails> {
  try {
    const parsed: unknown = await response.json();
    if (parsed && typeof parsed === 'object') {
      const { title, detail, type, instance } = parsed as Record<string, unknown>;
      const text = (value: unknown) => (typeof value === 'string' ? value : undefined);
      return Object.fromEntries(
        Object.entries({ status: response.status, title: text(title), detail: text(detail), type: text(type), instance: text(instance) }).filter(([, v]) => v !== undefined),
      ) as ProblemDetails;
    }
  } catch {
    // Not JSON (a proxy's HTML page): only the status is known.
  }
  return { status: response.status };
}
