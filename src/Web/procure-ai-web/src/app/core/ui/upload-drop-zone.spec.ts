import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { UploadDropZone } from './upload-drop-zone';

@Component({
  imports: [UploadDropZone],
  template: `<app-upload-drop-zone accept=".pdf,.md,.markdown" [maxBytes]="1000" [busy]="busy()" (fileSelected)="picked.push($event)" />`,
})
class Host {
  readonly busy = signal(false);
  readonly picked: File[] = [];
}

describe('UploadDropZone', () => {
  const file = (name: string, size: number) => new File([new Uint8Array(size)], name);

  function setup() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector('input[type=file]') as HTMLInputElement;
    return { fixture, el, input, host: fixture.componentInstance };
  }
  const choose = (input: HTMLInputElement, f: File) => {
    Object.defineProperty(input, 'files', { value: [f], configurable: true });
    input.dispatchEvent(new Event('change'));
  };

  it('drop_zone_keyboard_opens_picker: Enter and Space on the zone open the file picker', () => {
    const { el, input } = setup();
    const open = vi.spyOn(input, 'click').mockImplementation(() => undefined);
    const zone = el.querySelector('[data-testid="drop-zone"]') as HTMLElement;
    expect(zone.getAttribute('role')).toBe('button');
    expect(zone.tabIndex).toBe(0);
    zone.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    zone.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true }));
    expect(open).toHaveBeenCalledTimes(2);
  });

  it('oversized_file_rejected_client_side: nothing is emitted and the reason is shown', () => {
    const { fixture, el, input, host } = setup();
    choose(input, file('big.pdf', 1001));
    fixture.detectChanges();
    expect(host.picked).toEqual([]);
    expect(el.querySelector('[role=alert]')?.textContent).toContain('larger than');
  });

  it('a wrong type is rejected, a good file is emitted and clears the reason', () => {
    const { fixture, el, input, host } = setup();
    choose(input, file('tool.exe', 10));
    fixture.detectChanges();
    expect(host.picked).toEqual([]);
    expect(el.querySelector('[role=alert]')?.textContent).toContain('PDF');

    const good = file('policy.MD', 10);
    choose(input, good);
    fixture.detectChanges();
    expect(host.picked).toEqual([good]);
    expect(el.querySelector('[role=alert]')).toBeNull();
  });

  it('a dropped file is handled like a chosen one', () => {
    const { el, host } = setup();
    const good = file('policy.pdf', 10);
    const drop = new Event('drop', { bubbles: true, cancelable: true }) as Event & { dataTransfer?: unknown };
    drop.dataTransfer = { files: [good] };
    (el.querySelector('[data-testid="drop-zone"]') as HTMLElement).dispatchEvent(drop);
    expect(host.picked).toEqual([good]);
  });

  it('while busy nothing opens or is accepted', () => {
    const { fixture, el, input, host } = setup();
    host.busy.set(true);
    fixture.detectChanges();
    const open = vi.spyOn(input, 'click').mockImplementation(() => undefined);
    (el.querySelector('[data-testid="drop-zone"]') as HTMLElement).click();
    expect(open).not.toHaveBeenCalled();
    expect(el.querySelector('[aria-busy=true]')).not.toBeNull();
  });
});
