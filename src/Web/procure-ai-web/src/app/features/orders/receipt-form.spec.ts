import { TestBed } from '@angular/core/testing';
import { ReceiptForm } from './receipt-form';
import { order, orderLine } from './orders.testing';

describe('ReceiptForm', () => {
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function setup(o = order({ status: 'PartiallyReceived', lines: [orderLine(1, 5, 2), orderLine(2, 4), orderLine(3, 2, 2)] }), serverErrors: Record<string, string[]> = {}) {
    const fixture = TestBed.createComponent(ReceiptForm);
    fixture.componentRef.setInput('order', o);
    fixture.componentRef.setInput('serverErrors', serverErrors);
    const sent: unknown[] = [];
    fixture.componentInstance.submitted.subscribe((v) => sent.push(v));
    fixture.detectChanges();
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const inputs = () => Array.from(el.querySelectorAll<HTMLInputElement>('input[type=number]'));
    const type = async (input: HTMLInputElement, value: string) => {
      input.value = value;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      await fixture.whenStable();
    };
    const submit = async () => {
      (el.querySelector('button[type=submit]') as HTMLButtonElement).click();
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    return { fixture, el, inputs, type, submit, sent };
  }

  it('offers a quantity only for lines that still have something outstanding, with the outstanding shown', async () => {
    const { inputs, el } = await setup();
    expect(inputs()).toHaveLength(2);
    expect(text(el)).toContain('3 outstanding');
    expect(text(el)).toContain('4 outstanding');
    expect(text(el)).not.toContain('Item 3');
  });

  it('receipt_refuses_more_than_outstanding', async () => {
    const { inputs, type, submit, sent, el } = await setup();
    await type(inputs()[0], '4');
    await submit();
    expect(sent).toHaveLength(0);
    expect(text(el)).toContain('Only 3 outstanding');
  });

  it('receipt_needs_one_positive_line', async () => {
    const { submit, sent, el } = await setup();
    await submit();
    expect(sent).toHaveLength(0);
    expect(text(el)).toContain('Enter a quantity for at least one item');
  });

  it('refuses a negative or fractional quantity', async () => {
    const { inputs, type, submit, sent, el } = await setup();
    await type(inputs()[0], '-1');
    await submit();
    expect(sent).toHaveLength(0);
    await type(inputs()[0], '1.5');
    await submit();
    expect(sent).toHaveLength(0);
    expect(text(el)).toContain('whole number');
  });

  it('sends only the lines with a quantity and the delivery note', async () => {
    const { inputs, type, submit, sent, el } = await setup();
    await type(inputs()[1], '4');
    (el.querySelector('input[type=text]') as HTMLInputElement).value = ' DN-77 ';
    (el.querySelector('input[type=text]') as HTMLInputElement).dispatchEvent(new Event('input'));
    await submit();
    expect(sent).toEqual([{ deliveryNote: 'DN-77', lines: [{ lineNo: 2, quantity: 4 }] }]);
  });

  it('shows what the server said about a line', async () => {
    const { el } = await setup(undefined, { 'line.1': ['Quantity exceeds what is outstanding.'] });
    expect(text(el)).toContain('Quantity exceeds what is outstanding.');
  });
});
