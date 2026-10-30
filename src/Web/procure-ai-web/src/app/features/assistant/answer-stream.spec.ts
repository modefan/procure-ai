import { HttpXsrfTokenExtractor } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { AuthStore } from '../../core/auth/auth.store';
import { SessionExpired } from '../../core/auth/session-expired';
import { AppConfigService } from '../../core/config/app-config';
import { CORRELATION_HEADER } from '../../core/http/correlation.interceptor';
import { XSRF_HEADER_NAME } from '../../core/http/xsrf';
import { AnswerStream, FETCH, parseSse, SseEvent, StreamProblem } from './answer-stream';

const encoder = new TextEncoder();

/** A body that delivers exactly these chunks (strings are UTF-8 encoded; byte arrays are sent as they are). */
function body(chunks: (string | Uint8Array)[]): ReadableStream<Uint8Array> {
  return new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(typeof chunk === 'string' ? encoder.encode(chunk) : chunk);
      }
      controller.close();
    },
  });
}

async function collect(events: AsyncIterable<SseEvent>): Promise<SseEvent[]> {
  const out: SseEvent[] = [];
  for await (const event of events) {
    out.push(event);
  }
  return out;
}

describe('parseSse', () => {
  it('parser_handles_events_split_across_chunks', async () => {
    // "✓" is three bytes in UTF-8; the chunk boundary falls inside it.
    const tick = encoder.encode('{"text":"Finance ✓"}\n\n');
    const cut = tick.indexOf(0xe2) + 1;
    const events = await collect(
      parseSse(
        body([
          'event: sour',
          'ces\ndata: [{"ref":"S1","title":"Procurement Policy","sectionPath":["Procurement Policy","4.2 Approval thresholds"],"reference":null,"type":"Policy"}]\n\n',
          'event: delta\r\ndata: {"text":"No. "}\r\n\r\n',
          ': a comment line is ignored\n\nevent: delta\ndata: ',
          tick.slice(0, cut),
          tick.slice(cut),
          'event: unknown_event\ndata: {}\n\n',
          'event: done\ndata: {"messageId":"m1","usage":{"input":10,"output":4},"model":"fake-chat"}\n\n',
        ]),
      ),
    );

    expect(events).toEqual([
      {
        type: 'sources',
        data: [{ ref: 'S1', title: 'Procurement Policy', sectionPath: ['Procurement Policy', '4.2 Approval thresholds'], reference: null, type: 'Policy' }],
      },
      { type: 'delta', data: { text: 'No. ' } },
      { type: 'delta', data: { text: 'Finance ✓' } },
      { type: 'done', data: { messageId: 'm1', usage: { input: 10, output: 4 }, model: 'fake-chat' } },
    ]);
  });

  it('an event still waiting for its blank line when the stream ends is dispatched', async () => {
    const events = await collect(parseSse(body(['event: delta\ndata: {"text":"a"}\n'])));
    expect(events).toEqual([{ type: 'delta', data: { text: 'a' } }]);
  });

  it('broken JSON data is skipped, not thrown', async () => {
    const events = await collect(parseSse(body(['event: delta\ndata: {"text":\n\nevent: delta\ndata: {"text":"ok"}\n\n'])));
    expect(events).toEqual([{ type: 'delta', data: { text: 'ok' } }]);
  });
});

describe('AnswerStream', () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  const sessionExpired = { show: vi.fn() };

  beforeEach(() => {
    fetchMock = vi.fn();
    sessionExpired.show.mockClear();
    TestBed.configureTestingModule({
      providers: [
        { provide: FETCH, useValue: fetchMock },
        { provide: HttpXsrfTokenExtractor, useValue: { getToken: () => 'xsrf-1' } },
        { provide: SessionExpired, useValue: sessionExpired },
        { provide: AuthStore, useValue: { isLoggedIn: () => true } },
        { provide: Router, useValue: { url: '/app/assistant' } },
      ],
    });
  });

  it('posts the question with credentials, the XSRF header, a correlation id and the JSON body, and yields the events', async () => {
    fetchMock.mockResolvedValue(new Response(body(['event: delta\ndata: {"text":"Hi"}\n\n']), { status: 200, headers: { 'Content-Type': 'text/event-stream' } }));
    const controller = new AbortController();

    const events = await collect(TestBed.inject(AnswerStream).streamAnswer('c-1', { text: 'Hello?', mode: 'New' }, controller.signal));

    expect(events).toEqual([{ type: 'delta', data: { text: 'Hi' } }]);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/v1/assistant/conversations/c-1/messages');
    expect(init.method).toBe('POST');
    expect(init.credentials).toBe('same-origin');
    expect(init.signal).toBe(controller.signal);
    expect(JSON.parse(init.body as string)).toEqual({ text: 'Hello?', mode: 'New' });
    const headers = new Headers(init.headers);
    expect(headers.get(XSRF_HEADER_NAME)).toBe('xsrf-1');
    expect(headers.get(CORRELATION_HEADER)).toMatch(/^[0-9a-f-]{36}$/);
    expect(headers.get('Accept')).toBe('text/event-stream');
    expect(headers.get('Content-Type')).toBe('application/json');
  });

  it('a gateway on another origin gets the absolute url and the cookies (credentials include)', async () => {
    TestBed.inject(AppConfigService).config.update((c) => ({ ...c, apiBaseUrl: 'https://localhost:7100' }));
    fetchMock.mockResolvedValue(new Response(body([]), { status: 200 }));

    await collect(TestBed.inject(AnswerStream).streamAnswer('c 1', { text: 'Q', mode: 'New' }, new AbortController().signal));

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('https://localhost:7100/api/v1/assistant/conversations/c%201/messages');
    expect(init.credentials).toBe('include');
  });

  it('a non-2xx answer before the stream is a StreamProblem with the ProblemDetails', async () => {
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ status: 429, title: "You've used all 50 questions for today.", detail: 'The count starts again at midnight.' }), {
        status: 429,
        headers: { 'Content-Type': 'application/problem+json' },
      }),
    );

    const error = await collect(TestBed.inject(AnswerStream).streamAnswer('c-1', { text: 'Q', mode: 'New' }, new AbortController().signal)).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(StreamProblem);
    expect((error as StreamProblem).status).toBe(429);
    expect((error as StreamProblem).problem.title).toBe("You've used all 50 questions for today.");
    expect(sessionExpired.show).not.toHaveBeenCalled();
  });

  it('a 401 opens the session-expired dialog and a body that is not JSON still gives a StreamProblem', async () => {
    fetchMock.mockResolvedValue(new Response('<html>nope</html>', { status: 401 }));

    const error = await collect(TestBed.inject(AnswerStream).streamAnswer('c-1', { text: 'Q', mode: 'New' }, new AbortController().signal)).catch((e: unknown) => e);

    expect((error as StreamProblem).status).toBe(401);
    expect((error as StreamProblem).problem).toEqual({ status: 401 });
    expect(sessionExpired.show).toHaveBeenCalledWith('/app/assistant');
  });

  it('abort cancels the running read', async () => {
    let cancelled = false;
    const open = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoder.encode('event: delta\ndata: {"text":"a"}\n\n'));
      },
      cancel() {
        cancelled = true;
      },
    });
    fetchMock.mockResolvedValue(new Response(open, { status: 200 }));
    const controller = new AbortController();
    const seen: SseEvent[] = [];

    const done = (async () => {
      for await (const event of TestBed.inject(AnswerStream).streamAnswer('c-1', { text: 'Q', mode: 'New' }, controller.signal)) {
        seen.push(event);
        controller.abort();
      }
    })();

    await expect(done).rejects.toMatchObject({ name: 'AbortError' });
    expect(seen).toEqual([{ type: 'delta', data: { text: 'a' } }]);
    expect(cancelled).toBe(true);
  });
});
