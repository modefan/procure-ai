import { TestBed } from '@angular/core/testing';
import { FieldChangeDto } from '../../api/audit/models/field-change-dto';
import { FieldDiff } from './field-diff';

describe('FieldDiff', () => {
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  function render(fields: FieldChangeDto[], operation: string) {
    const fixture = TestBed.createComponent(FieldDiff);
    fixture.componentRef.setInput('fields', fields);
    fixture.componentRef.setInput('operation', operation);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('diff_shows_old_new_and_masked', () => {
    const el = render(
      [
        { column: 'title', old: '3 laptops', new: '4 laptops' },
        { column: 'api_key', old: '***', new: '***' },
        { column: 'note', old: null, new: 'urgent' },
      ],
      'Update',
    );
    expect(text(el.querySelector('[data-value="title"]'))).toBe('3 laptops → 4 laptops');
    expect(text(el.querySelector('[data-value="api_key"]'))).toBe('••• → •••');
    expect(el.textContent).not.toContain('***');
    expect(text(el.querySelector('[data-value="note"]'))).toBe('empty → urgent');
    expect(el.querySelector('[data-testid="diff-operation"]')).toBeNull();
  });

  it('shows an insert as new and a delete as removed', () => {
    const inserted = render([{ column: 'title', old: null, new: 'Chairs' }], 'Insert');
    expect(text(inserted.querySelector('[data-testid="diff-operation"]'))).toBe('new');
    expect(text(inserted.querySelector('[data-value="title"]'))).toBe('Chairs');
    TestBed.resetTestingModule();

    const deleted = render([{ column: 'title', old: 'Chairs', new: null }], 'Delete');
    expect(text(deleted.querySelector('[data-testid="diff-operation"]'))).toBe('removed');
    expect(text(deleted.querySelector('[data-value="title"]'))).toBe('Chairs');
  });
});
