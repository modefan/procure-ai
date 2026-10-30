import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ApprovalsService } from '../../api/approvals/services/approvals.service';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { ApprovalsPage } from './approvals.page';
import { ApprovalsStore } from './approvals.store';
import { inboxPage, inboxStep } from './approvals.testing';

describe('ApprovalsPage', () => {
  let api: Record<string, ReturnType<typeof vi.fn>>;
  let live: LiveConnection;
  let ask: ReturnType<typeof vi.fn>;
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function setup(items = [inboxStep(), inboxStep({ id: 's2', requestId: 'r2', requestNumber: 'PR-1045', requestTitle: 'Office chairs', version: 77, dueAt: null })]) {
    api = {
      getApprovalInbox: vi.fn().mockResolvedValue(inboxPage(items)),
      approveStep: vi.fn().mockResolvedValue(inboxStep({ status: 'Approved' })),
      rejectStep: vi.fn().mockResolvedValue(inboxStep({ status: 'Rejected' })),
    };
    ask = vi.fn().mockResolvedValue({ confirmed: true, value: '' });
    const fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApprovalsService, useValue: { getApprovalInbox: api['getApprovalInbox'], approveStep: api['approveStep'], rejectStep: api['rejectStep'] } },
        { provide: ConfirmDialog, useValue: { askWithInput: ask } },
        { provide: ConflictDialog, useValue: { show: vi.fn().mockResolvedValue(undefined) } },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
    live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    const fixture = TestBed.createComponent(ApprovalsPage);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }

  afterEach(() => void live?.stop());

  it('lists number (link), title, requester, amount, waiting time and escalation', async () => {
    const { el } = await setup();
    const rows = Array.from(el.querySelectorAll('tbody tr[data-step]'));
    expect(rows).toHaveLength(2);
    const first = text(rows[0]);
    expect(first).toContain('PR-1044');
    expect(first).toContain('Design software licences');
    expect(first).toContain('Ana Smith');
    expect(first).toContain('R 49 680,00');
    expect(first).toContain('2 h');
    expect(rows[0].querySelector('a')?.getAttribute('href')).toBe('/app/requests/r1');
    expect(first).toContain('Approve');
    expect(first).toContain('Reject');
  });

  it('approve button sends the approval and the row goes away', async () => {
    const { fixture, el } = await setup();
    api['getApprovalInbox'].mockResolvedValue(inboxPage([inboxStep({ id: 's2', requestId: 'r2', requestNumber: 'PR-1045', version: 77 })]));
    (el.querySelector('tr[data-step] [data-action="approve"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(api['approveStep']).toHaveBeenCalledWith(expect.objectContaining({ stepId: 's1', 'If-Match': '"1730"' }), expect.anything());
    expect(el.querySelectorAll('tbody tr[data-step]')).toHaveLength(1);
  });

  it('approve_sends_optional_comment', async () => {
    const { fixture, el } = await setup();
    ask.mockResolvedValueOnce({ confirmed: true, value: 'Within budget, go ahead' });
    (el.querySelector('tr[data-step] [data-action="approve"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(ask).toHaveBeenCalledWith(expect.objectContaining({ confirm: 'Approve', title: expect.stringContaining('PR-1044'), input: { label: 'Comment (optional)', minLength: 0, maxLength: 500, required: false } }));
    expect(api['approveStep']).toHaveBeenCalledWith(expect.objectContaining({ stepId: 's1', body: { comment: 'Within budget, go ahead' } }), expect.anything());
  });

  it('approving without a comment sends no body, and backing out sends nothing', async () => {
    const { fixture, el } = await setup();
    (el.querySelector('tr[data-step] [data-action="approve"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect((api['approveStep'].mock.calls[0][0] as Record<string, unknown>)['body']).toBeUndefined();

    ask.mockResolvedValueOnce({ confirmed: false, value: 'typed but dropped' });
    (el.querySelector('tr[data-step] [data-action="approve"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(api['approveStep']).toHaveBeenCalledTimes(1);
  });

  it('a row shows a busy state while the server works', async () => {
    const { fixture, el } = await setup();
    let finish!: () => void;
    api['approveStep'].mockReturnValue(new Promise((resolve) => (finish = () => resolve(inboxStep()))));
    (el.querySelector('tr[data-step] [data-action="approve"] button') as HTMLButtonElement).click();
    await vi.waitFor(() => expect(api['approveStep']).toHaveBeenCalled()); // after the comment dialog answered
    fixture.detectChanges();
    const row = el.querySelector('tbody tr[data-step]') as HTMLElement;
    expect(text(row)).toContain('Approving');
    expect(row.querySelector<HTMLButtonElement>('[data-action="approve"] button')?.disabled).toBe(true);
    expect(row.querySelector<HTMLButtonElement>('[data-action="reject"] button')?.disabled).toBe(true);
    finish();
    await settle(fixture);
  });

  it('reject asks for a reason, will not send without one, then rejects', async () => {
    const { fixture, el } = await setup();
    (el.querySelector('tr[data-step] [data-action="reject"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    const confirm = el.querySelector('[data-action="confirm-reject"] button') as HTMLButtonElement;
    confirm.click();
    fixture.detectChanges();
    expect(api['rejectStep']).not.toHaveBeenCalled();
    expect(text(el)).toContain('Enter a reason for rejecting.');

    const reason = el.querySelector('textarea') as HTMLTextAreaElement;
    reason.value = 'Over budget';
    reason.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(text(el)).not.toContain('Enter a reason for rejecting.'); // typing clears the required error
    (el.querySelector('[data-action="confirm-reject"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(api['rejectStep']).toHaveBeenCalledWith(expect.objectContaining({ stepId: 's1', body: { reason: 'Over budget' } }), expect.anything());
  });

  it('the reject box goes away when its row leaves the list', async () => {
    const { fixture, el } = await setup();
    (el.querySelector('tr[data-step] [data-action="reject"] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(el.querySelector('textarea')).not.toBeNull();

    api['getApprovalInbox'].mockResolvedValue(inboxPage([inboxStep({ id: 's2', requestId: 'r2', requestNumber: 'PR-1045' })]));
    TestBed.inject(ApprovalsStore).invalidate('list'); // a push told us the first row is gone
    await settle(fixture);

    expect(el.querySelectorAll('tbody tr[data-step]')).toHaveLength(1);
    expect(el.querySelector('textarea')).toBeNull();
  });

  it('a refusal is shown on its row', async () => {
    const { fixture, el } = await setup();
    api['approveStep'].mockRejectedValue(new HttpErrorResponse({ status: 403, error: { detail: 'You are not an approver of this step' } }));
    (el.querySelector('tr[data-step] [data-action="approve"] button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(text(el.querySelector('tbody tr[data-extra]'))).toContain('You are not an approver of this step');
    expect(el.querySelectorAll('tbody tr[data-step]')).toHaveLength(2);
  });

  it('an empty inbox says so', async () => {
    const { el } = await setup([]);
    expect(text(el)).toContain('Nothing is waiting for your approval');
    expect(el.querySelector('table')).toBeNull();
  });

  it('a failed load shows the error with a retry', async () => {
    api = undefined as never;
    const fake = fakeHubFactory();
    const getApprovalInbox = vi.fn().mockRejectedValue(new HttpErrorResponse({ status: 503 }));
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ApprovalsService, useValue: { getApprovalInbox } },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
    live = TestBed.inject(LiveConnection);
    const fixture = TestBed.createComponent(ApprovalsPage);
    fixture.detectChanges();
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role="alert"]')).not.toBeNull();
    expect(text(el)).toContain('Try again');
  });
});
