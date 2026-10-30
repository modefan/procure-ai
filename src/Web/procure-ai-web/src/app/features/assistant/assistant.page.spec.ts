import { TestBed } from '@angular/core/testing';
import { AssistantInfoService } from '../../api/assistant/services/assistant-info.service';
import { ConversationsService } from '../../api/assistant/services/conversations.service';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { AnswerStream } from './answer-stream';
import { AssistantPage } from './assistant.page';
import { FakeAnswerStream, fakeConversationsApi, fakeInfoApi, flush } from './assistant.testing';

describe('AssistantPage', () => {
  let streams: FakeAnswerStream;
  const text = (el: Element | null | undefined) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function setup(quota = { used: 12, limit: 50 }) {
    streams = new FakeAnswerStream();
    TestBed.configureTestingModule({
      providers: [
        { provide: ConversationsService, useValue: fakeConversationsApi({ quota }) },
        { provide: AssistantInfoService, useValue: fakeInfoApi() },
        { provide: AnswerStream, useValue: streams },
        { provide: ConfirmDialog, useValue: { ask: vi.fn().mockResolvedValue(true) } },
      ],
    });
    const fixture = TestBed.createComponent(AssistantPage);
    const el = fixture.nativeElement as HTMLElement;
    const settle = async () => {
      for (let i = 0; i < 4; i++) {
        await flush(1);
        await fixture.whenStable();
        fixture.detectChanges();
      }
    };
    fixture.detectChanges();
    await settle();
    const question = () => el.querySelector<HTMLTextAreaElement>('[data-testid="question"]')!;
    const type = async (value: string) => {
      question().value = value;
      question().dispatchEvent(new Event('input'));
      await settle();
    };
    const press = async (init: KeyboardEventInit) => {
      const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true, ...init });
      question().dispatchEvent(event);
      await settle();
      return event;
    };
    return { fixture, el, settle, question, type, press };
  }

  it('shows_the_footer_quota_and_the_demo_model_badge', async () => {
    const { el } = await setup();
    expect(text(el.querySelector('[data-testid="footer"]'))).toBe(
      "Answers come only from your company's policy and past purchases. 12 of 50 questions used today.",
    );
    expect(text(el.querySelector('[data-testid="demo-model"]'))).toBe('Demo model');
  });

  it('enter_sends_shift_enter_newline', async () => {
    const { press, type, question } = await setup();
    await type('Do I need finance approval?');

    const newline = await press({ shiftKey: true });
    expect(newline.defaultPrevented).toBe(false);
    expect(streams.streams.length).toBe(0);

    const send = await press({});
    expect(send.defaultPrevented).toBe(true);
    expect(streams.streams.length).toBe(1);
    expect(streams.last.body).toEqual({ text: 'Do I need finance approval?', mode: 'New' });

    // While an IME is composing, Enter belongs to the IME.
    streams.last.emit({ type: 'done', data: { messageId: 'm', usage: { input: 1, output: 1 }, model: 'fake-chat' } });
    await press({});
    await type('Next');
    const composing = await press({ isComposing: true });
    expect(composing.defaultPrevented).toBe(false);
    expect(streams.streams.length).toBe(1);
    expect(question().value).toBe('Next');
  });

  it('send_disabled_while_streaming', async () => {
    const { el, type, press, question, settle } = await setup();
    await type('Do I need finance approval?');
    await press({});
    streams.last.emit({ type: 'delta', data: { text: 'No.' } });
    await settle();

    expect(question().disabled).toBe(true);
    expect(el.querySelector('[data-testid="send"]')).toBeNull();
    const stop = el.querySelector<HTMLButtonElement>('[data-testid="stop"]')!;
    expect(stop).not.toBeNull();
    // The question left the box once the server took it.
    expect(question().value).toBe('');

    // A second Enter while streaming sends nothing.
    await press({});
    expect(streams.streams.length).toBe(1);

    stop.click();
    await settle();
    expect(streams.last.signal.aborted).toBe(true);
    expect(question().disabled).toBe(false);
    expect(el.querySelector('[data-testid="send"]')).not.toBeNull();
    expect(text(el.querySelector('[data-testid="message-status"]'))).toBe('Stopped');
  });

  it('quota_429_shows_message_and_disables_input', async () => {
    const { el, type, press, question, settle } = await setup({ used: 49, limit: 50 });
    await type('One more?');
    await press({});
    streams.last.refuse(429, { title: "You've used all 50 questions for today.", detail: 'The count starts again at midnight.' });
    await settle();

    const alert = el.querySelector('[data-testid="notice"]');
    expect(text(alert)).toContain("You've used all 50 questions for today.");
    expect(text(alert)).toContain('The count starts again at midnight.');
    expect(question().disabled).toBe(true);
    expect(el.querySelector<HTMLButtonElement>('[data-testid="send"]')!.disabled).toBe(true);
    expect(text(el.querySelector('[data-testid="footer"]'))).toContain('50 of 50 questions used today.');
  });

  it('autoscroll_pauses_when_user_scrolls_up', async () => {
    const { el, type, press, settle } = await setup();
    const scroller = el.querySelector<HTMLElement>('[data-testid="messages"]')!;
    let top = 0;
    let height = 1000;
    Object.defineProperty(scroller, 'scrollHeight', { configurable: true, get: () => height });
    Object.defineProperty(scroller, 'clientHeight', { configurable: true, get: () => 400 });
    Object.defineProperty(scroller, 'scrollTop', { configurable: true, get: () => top, set: (v: number) => (top = v) });

    await type('Do I need finance approval?');
    await press({});
    streams.last.emit({ type: 'delta', data: { text: 'No. ' } });
    await settle();
    expect(top).toBe(1000);
    expect(el.querySelector('[data-testid="jump-latest"]')).toBeNull();

    // The user scrolls up to read: new text no longer pulls the view down.
    top = 200;
    scroller.dispatchEvent(new Event('scroll'));
    height = 1400;
    streams.last.emit({ type: 'delta', data: { text: 'Finance approval is only needed above R 200 000.' } });
    await settle();
    expect(top).toBe(200);
    const jump = el.querySelector<HTMLButtonElement>('[data-testid="jump-latest"]')!;
    expect(text(jump)).toBe('Jump to latest');

    jump.click();
    await settle();
    expect(top).toBe(1400);
    expect(el.querySelector('[data-testid="jump-latest"]')).toBeNull();

    // Following again: more text scrolls with it.
    height = 1600;
    streams.last.emit({ type: 'delta', data: { text: ' More.' } });
    await settle();
    expect(top).toBe(1600);
  });
});
