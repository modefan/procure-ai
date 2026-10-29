import { TestBed } from '@angular/core/testing';
import { BusinessEventDto } from '../../api/audit/models/business-event-dto';
import { AuditTrail } from './audit-trail';

describe('AuditTrail', () => {
  const event = (summary: string, occurredAt: string): BusinessEventDto => ({ type: 'BudgetReserved', summary, occurredAt }) as BusinessEventDto;

  it('shows newest first and keeps the R of an amount with its number so a sentence never wraps between them', () => {
    const fixture = TestBed.createComponent(AuditTrail);
    fixture.componentRef.setInput('events', [event('Older', '2026-10-29T08:00:00Z'), event('R 86 940,00 reserved on IT-200', '2026-10-29T09:00:00Z')]);
    fixture.detectChanges();
    const rows = [...fixture.nativeElement.querySelectorAll('tbody tr')] as HTMLElement[];
    expect(rows[0].textContent).toContain('R\u00a086 940,00 reserved on IT-200');
    expect(rows[0].textContent).not.toContain('R 86');
    expect(rows[1].textContent).toContain('Older');
  });

  it('says so when nothing has happened yet', () => {
    const fixture = TestBed.createComponent(AuditTrail);
    fixture.componentRef.setInput('events', []);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Nothing has happened');
  });
});
