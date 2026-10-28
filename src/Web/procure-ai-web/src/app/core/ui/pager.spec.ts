import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Pager } from './pager';

@Component({
  imports: [Pager],
  template: `<app-pager [page]="page()" [pageCount]="3" [total]="41" [loading]="loading()" noun="request" nounPlural="requests" (pageChange)="changes.push($event)" />`,
})
class Host {
  readonly page = signal(1);
  readonly loading = signal(false);
  readonly changes: number[] = [];
}

describe('Pager', () => {
  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const buttons = () => [...el.querySelectorAll('nav[aria-label="Pagination"] button')] as HTMLButtonElement[];
    return { fixture, el, buttons };
  }

  it('announces the page and the total', () => {
    const { el } = render();
    expect(el.textContent!.replace(/\s+/g, ' ')).toContain('Page 1 of 3 (41 requests)');
  });

  it('disables Previous on the first page and emits the next page number', () => {
    const { fixture, buttons } = render();
    const [previous, next] = buttons();
    expect(previous.disabled).toBe(true);

    next.click();

    expect(fixture.componentInstance.changes).toEqual([2]);
  });

  it('disables Next on the last page and emits the previous one', () => {
    const { fixture, buttons } = render();
    fixture.componentInstance.page.set(3);
    fixture.detectChanges();
    const [previous, next] = buttons();
    expect(next.disabled).toBe(true);

    previous.click();

    expect(fixture.componentInstance.changes).toEqual([2]);
  });

  it('disables both buttons while loading', () => {
    const { fixture, buttons } = render();
    fixture.componentInstance.page.set(2);
    fixture.componentInstance.loading.set(true);
    fixture.detectChanges();

    expect(buttons().every((b) => b.disabled)).toBe(true);
  });
});
