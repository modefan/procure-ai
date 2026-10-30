import { TestBed } from '@angular/core/testing';
import { ConfirmDialog, ConfirmDialogHost } from './confirm-dialog';

describe('ConfirmDialog', () => {
  beforeEach(() => {
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    };
  });

  async function ask() {
    const fixture = TestBed.createComponent(ConfirmDialogHost);
    fixture.detectChanges();
    await fixture.whenStable();
    const answer = TestBed.inject(ConfirmDialog).ask({ title: 'Cancel PR-1001?', body: 'The budget is released.', confirm: 'Cancel request', cancel: 'Keep it' });
    fixture.detectChanges();
    await fixture.whenStable();
    const buttons = [...fixture.nativeElement.querySelectorAll('dialog button')] as HTMLButtonElement[];
    return { fixture, answer, buttons };
  }

  it('shows title, body and both labels', async () => {
    const { fixture, buttons } = await ask();

    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Cancel PR-1001?');
    expect(text).toContain('The budget is released.');
    expect(buttons.map((b) => b.textContent!.trim())).toEqual(['Keep it', 'Cancel request']);
  });

  it('resolves true on confirm', async () => {
    const { answer, buttons } = await ask();
    buttons[1].click();
    await expect(answer).resolves.toBe(true);
  });

  it('resolves false on cancel', async () => {
    const { answer, buttons } = await ask();
    buttons[0].click();
    await expect(answer).resolves.toBe(false);
  });

  it('resolves false when closed with Esc', async () => {
    const { fixture, answer } = await ask();
    (fixture.nativeElement.querySelector('dialog') as HTMLDialogElement).close();
    await expect(answer).resolves.toBe(false);
  });
  describe('askWithInput', () => {
    async function askWithInput(input = { label: 'Reason', minLength: 3, maxLength: 20, required: true }) {
      const fixture = TestBed.createComponent(ConfirmDialogHost);
      fixture.detectChanges();
      await fixture.whenStable();
      const answer = TestBed.inject(ConfirmDialog).askWithInput({ title: 'Close short?', body: 'Final.', confirm: 'Close short', cancel: 'Keep open', input });
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      // The text box is imported on demand: wait until it is there.
      await vi.waitFor(() => {
        fixture.detectChanges();
        expect(el.querySelector('dialog textarea')).not.toBeNull();
      });
      const area = el.querySelector('dialog textarea') as HTMLTextAreaElement;
      const buttons = [...el.querySelectorAll('dialog button')] as HTMLButtonElement[];
      const type = async (value: string) => {
        area.value = value;
        area.dispatchEvent(new Event('input'));
        fixture.detectChanges();
        await fixture.whenStable();
      };
      return { fixture, el, answer, area, buttons, type };
    }

    it('ask_with_input_returns_value_and_enforces_min_length', async () => {
      const { fixture, el, answer, buttons, type, area } = await askWithInput();
      expect(el.querySelector('dialog label')?.textContent).toContain('Reason');
      expect(area.getAttribute('maxlength')).toBe('20');

      await type('ab'); // too short: the dialog stays open and says why
      buttons[1].click();
      fixture.detectChanges();
      await fixture.whenStable();
      expect(el.querySelector('dialog')?.hasAttribute('open')).toBe(true);
      expect(el.querySelector('dialog [role="alert"]')?.textContent).toContain('at least 3');

      await type('  Wrong supplier  ');
      buttons[1].click();
      await expect(answer).resolves.toEqual({ confirmed: true, value: 'Wrong supplier' });
    });

    it('an optional input may stay empty and still confirms', async () => {
      const { answer, buttons } = await askWithInput({ label: 'Comment (optional)', minLength: 0, maxLength: 500, required: false });
      buttons[1].click();
      await expect(answer).resolves.toEqual({ confirmed: true, value: '' });
    });

    it('cancel resolves not confirmed with an empty value, whatever was typed', async () => {
      const { answer, buttons, type } = await askWithInput();
      await type('half typed');
      buttons[0].click();
      await expect(answer).resolves.toEqual({ confirmed: false, value: '' });
    });
  });
});
