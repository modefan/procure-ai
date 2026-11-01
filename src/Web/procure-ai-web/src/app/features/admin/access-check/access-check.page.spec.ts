import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, Router } from '@angular/router';
import { AccessService } from '../../../api/access/services/access.service';
import { ApprovalsService } from '../../../api/approvals/services/approvals.service';
import { RequestsService } from '../../../api/requests/services/requests.service';
import { AuthStore } from '../../../core/auth/auth.store';
import { APP_ROUTES } from '../../app.routes';
import { checkDto, userDto } from '../admin.testing';
import { AccessCheckPage } from './access-check.page';

describe('AccessCheckPage', () => {
  let access: { listUsers: ReturnType<typeof vi.fn> };
  let approvals: { checkAuthorization: ReturnType<typeof vi.fn> };
  let requests: { getRequestByNumber: ReturnType<typeof vi.fn> };
  let router: { navigate: ReturnType<typeof vi.fn> };
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }

  async function setup(params: Record<string, string> = {}) {
    access = { listUsers: vi.fn().mockResolvedValue([userDto('maria'), userDto('tom', { displayName: 'Tom Mokoena', email: 'tom@acme.test', roles: ['requester'] })]) };
    approvals = { checkAuthorization: vi.fn().mockResolvedValue(checkDto()) };
    requests = { getRequestByNumber: vi.fn().mockResolvedValue({ id: 'req-1039', number: 'PR-1039' }) };
    router = { navigate: vi.fn().mockResolvedValue(true) };
    TestBed.configureTestingModule({
      providers: [
        { provide: AccessService, useValue: access },
        { provide: ApprovalsService, useValue: approvals },
        { provide: RequestsService, useValue: requests },
        { provide: Router, useValue: router },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(params) } } },
        { provide: AuthStore, useValue: { hasPermission: () => true, user: signal({ userId: 'admin', name: 'Admin' }) } },
      ],
    });
    const fixture = TestBed.createComponent(AccessCheckPage);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }
  type Fixture = Awaited<ReturnType<typeof setup>>['fixture'];

  async function fillAndCheck(fixture: Fixture, userText: string, action: string, request: string) {
    const el = fixture.nativeElement as HTMLElement;
    const user = el.querySelector<HTMLInputElement>('#check-user')!;
    user.focus();
    user.value = userText;
    user.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: userText }));
    fixture.detectChanges();
    await settle(fixture);
    const option = [...el.querySelectorAll<HTMLElement>('[role="option"]')].find((o) => text(o).includes(userText))!;
    option.click();
    await settle(fixture);
    const select = el.querySelector<HTMLSelectElement>('#check-action')!;
    select.value = action;
    select.dispatchEvent(new Event('change'));
    const input = el.querySelector<HTMLInputElement>('#check-request')!;
    input.value = request;
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
    el.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    await settle(fixture);
  }

  it('check_shows_denied_with_failed_step_marked: Denied, the failed gating step marked, the routing line apart', async () => {
    const { fixture, el } = await setup();
    await fillAndCheck(fixture, 'Maria', 'approve', 'pr-1039');
    expect(requests.getRequestByNumber).toHaveBeenCalledWith({ number: 'PR-1039' });
    expect(approvals.checkAuthorization).toHaveBeenCalledWith({ userId: 'maria', requestId: 'req-1039', action: 'approve' });
    const tile = el.querySelector('[data-testid="verdict"]');
    expect(text(tile)).toContain('Denied');
    expect(text(tile)).not.toContain('Allowed');
    const failed = el.querySelector('[data-step="separation"]')!;
    expect(failed.getAttribute('data-passed')).toBe('false');
    expect(text(failed)).toContain('Is not the requester of this request');
    expect(el.querySelector('[data-step="permission"]')!.getAttribute('data-passed')).toBe('true');
    // The routing line is explanation, not a reason: it sits under its own heading, outside the rule list.
    const explanation = el.querySelector('[data-testid="routing-explanation"]')!;
    expect(text(explanation)).toContain('Routing explanation');
    expect(text(explanation)).toContain('Routing today would not pick this user');
    expect(el.querySelector('[data-testid="rules"]')!.contains(el.querySelector('[data-step="cost-center"]'))).toBe(false);
    expect(text(el)).toContain('Runs the same authorization rules the APIs use, step by step.');
  });

  it('check_shows_allowed: Allowed with every rule passed', async () => {
    const { fixture, el } = await setup();
    approvals.checkAuthorization.mockResolvedValue(
      checkDto({ allowed: true, steps: [{ code: 'permission', passed: true, text: 'Has permission requests.approve', gating: true }] }),
    );
    await fillAndCheck(fixture, 'Maria', 'approve', '1039');
    expect(requests.getRequestByNumber).toHaveBeenCalledWith({ number: 'PR-1039' });
    expect(text(el.querySelector('[data-testid="verdict"]'))).toContain('Allowed');
    expect(el.querySelector('[data-testid="routing-explanation"]')).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith([], { queryParams: { user: 'maria', action: 'approve', request: 'PR-1039' }, replaceUrl: true });
  });

  it('unknown_request_number_shows_not_found: a 404 on the lookup says so and runs no check', async () => {
    const { fixture, el } = await setup();
    requests.getRequestByNumber.mockRejectedValue(new HttpErrorResponse({ status: 404 }));
    await fillAndCheck(fixture, 'Maria', 'view', 'PR-9999');
    expect(text(el.querySelector('[data-testid="not-found"]'))).toContain('Request PR-9999 not found.');
    expect(approvals.checkAuthorization).not.toHaveBeenCalled();
    expect(el.querySelector('[data-testid="verdict"]')).toBeNull();
  });

  it('query_params_prefill_and_run: a link reproduces the check', async () => {
    const { el } = await setup({ user: 'tom', action: 'view', request: 'PR-1039' });
    expect(approvals.checkAuthorization).toHaveBeenCalledWith({ userId: 'tom', requestId: 'req-1039', action: 'view' });
    expect(el.querySelector<HTMLInputElement>('#check-user')!.value).toBe('Tom Mokoena');
    expect(el.querySelector<HTMLSelectElement>('#check-action')!.value).toBe('view');
    expect(el.querySelector<HTMLInputElement>('#check-request')!.value).toBe('PR-1039');
    expect(text(el.querySelector('[data-testid="verdict"]'))).toContain('Denied');
  });

  it('changing_an_input_clears_the_old_verdict (final review F8): no verdict stays next to inputs it was not for', async () => {
    const { fixture, el } = await setup();
    await fillAndCheck(fixture, 'Maria', 'approve', 'pr-1039');
    expect(el.querySelector('[data-testid="verdict"]')).not.toBeNull();

    const input = el.querySelector<HTMLInputElement>('#check-request')!;
    input.value = 'PR-1040';
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
    expect(el.querySelector('[data-testid="verdict"]')).toBeNull();

    // Back to the checked request, then another action: the verdict is gone again until Check runs.
    el.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    await settle(fixture);
    expect(el.querySelector('[data-testid="verdict"]')).not.toBeNull();
    const select = el.querySelector<HTMLSelectElement>('#check-action')!;
    select.value = 'view';
    select.dispatchEvent(new Event('input'));
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(el.querySelector('[data-testid="verdict"]')).toBeNull();
  });

  it('request_placeholder_is_a_neutral_example (final review F13)', async () => {
    const { el } = await setup();
    expect(el.querySelector<HTMLInputElement>('#check-request')!.placeholder).toBe('e.g. PR-1001');
  });

  it('missing_inputs_are_named_and_nothing_is_called', async () => {
    const { fixture, el } = await setup();
    el.querySelector<HTMLButtonElement>('button[type="submit"]')!.click();
    await settle(fixture);
    expect(text(el)).toContain('Choose a user from the list.');
    expect(text(el)).toContain('Enter a request number.');
    expect(requests.getRequestByNumber).not.toHaveBeenCalled();
  });
});

describe('APP_ROUTES access check', () => {
  it('page_hidden_without_admin_authorization: the route needs admin.authorization', async () => {
    const route = APP_ROUTES.find((r) => r.path === 'admin/access-check');
    expect(route).toBeDefined();
    expect(route?.data?.['permission']).toBe('admin.authorization');
    expect(route?.canActivate?.length).toBe(1);
    expect(await (route!.loadComponent as () => Promise<unknown>)()).toBeTruthy();
  });
});
