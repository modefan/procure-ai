import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { PolicyDocumentDto } from '../../api/assistant/models/policy-document-dto';
import { PolicyDocumentsService } from '../../api/assistant/services/policy-documents.service';
import { AuthStore } from '../../core/auth/auth.store';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { APP_ROUTES } from '../app.routes';
import { PoliciesPage } from './policies.page';
import { fileOf, policyDto } from './policies.testing';

describe('PoliciesPage', () => {
  let api: Record<'listPolicyDocuments' | 'uploadPolicyDocument' | 'replacePolicyDocumentFile' | 'removePolicyDocument', ReturnType<typeof vi.fn>>;
  let confirm: { ask: ReturnType<typeof vi.fn> };
  const text = (el: Element | null) => el?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

  async function settle(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    for (let i = 0; i < 5; i++) {
      await fixture.whenStable();
      await new Promise((r) => setTimeout(r));
      fixture.detectChanges();
    }
  }

  function providers(items: PolicyDocumentDto[], permissions: string[]) {
    api = {
      listPolicyDocuments: vi.fn().mockResolvedValue({ items, page: 1, pageSize: 100, total: items.length }),
      uploadPolicyDocument: vi.fn().mockResolvedValue(policyDto('doc-9', { status: 'Uploaded', progress: 0 })),
      replacePolicyDocumentFile: vi.fn().mockResolvedValue(policyDto()),
      removePolicyDocument: vi.fn().mockResolvedValue(undefined),
    };
    confirm = { ask: vi.fn().mockResolvedValue(true) };
    return [
      { provide: PolicyDocumentsService, useValue: api },
      { provide: AuthStore, useValue: { hasPermission: (p: string) => permissions.includes(p), user: signal({ userId: 'a', name: 'Admin' }) } },
      { provide: ConfirmDialog, useValue: confirm },
      { provide: ConflictDialog, useValue: { show: vi.fn().mockResolvedValue(undefined) } },
      { provide: LIVE_HUB_FACTORY, useValue: fakeHubFactory().factory },
    ];
  }

  async function setup(items: PolicyDocumentDto[] = [policyDto()]) {
    TestBed.configureTestingModule({ providers: providers(items, ['admin.policies']) });
    const fixture = TestBed.createComponent(PoliciesPage);
    fixture.detectChanges();
    await settle(fixture);
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }
  const row = (el: HTMLElement, id: string) => el.querySelector(`tbody tr[data-document="${id}"]`);

  it('shows document, version, chunks and the Ready badge, and the note row', async () => {
    const { el } = await setup();
    const r = text(row(el, 'doc-1'));
    expect(r).toContain('Procurement Policy 2026');
    expect(r).toContain('3');
    expect(r).toContain('148');
    expect(r).toContain('Ready');
    expect(text(el)).toContain('Past purchases are added automatically when a request is completed.');
  });

  it('embedding row shows the progress through the --progress custom property', async () => {
    const { el } = await setup([policyDto('doc-2', { status: 'Embedding', progress: 40, chunkCount: 0 })]);
    const bar = el.querySelector('[data-progress]') as HTMLElement;
    expect(bar.style.getPropertyValue('--progress')).toBe('40%');
    expect(text(row(el, 'doc-2'))).toContain('Embedding… 40%');
    expect(bar.getAttribute('style')).not.toContain('width');
  });

  it('flagged_shows_reason and failed shows its reason', async () => {
    const { el } = await setup([
      policyDto('f1', { status: 'Flagged', flaggedReason: 'Looks like an instruction to the assistant.' }),
      policyDto('x1', { status: 'Failed', failureReason: 'The file has no readable text.' }),
    ]);
    expect(text(row(el, 'f1'))).toContain('Flagged');
    expect(text(row(el, 'f1'))).toContain('Looks like an instruction to the assistant.');
    expect(text(row(el, 'x1'))).toContain('Failed');
    expect(text(row(el, 'x1'))).toContain('The file has no readable text.');
  });

  it('empty list shows the empty state and still the drop zone', async () => {
    const { el } = await setup([]);
    expect(text(el)).toContain('No policy documents yet');
    expect(el.querySelector('[data-testid="drop-zone"]')).not.toBeNull();
  });

  it('remove_asks_then_deletes: the Remove button asks first, then sends If-Match', async () => {
    const { fixture, el } = await setup();
    (row(el, 'doc-1')!.querySelector('[data-action="remove"]') as HTMLElement).click();
    await settle(fixture);
    expect(confirm.ask).toHaveBeenCalled();
    expect(api.removePolicyDocument).toHaveBeenCalledWith({ id: 'doc-1', 'If-Match': '"7"' });
  });

  it('replace_sends_if_match: choosing a file after Replace sends it with the row version', async () => {
    const { fixture, el } = await setup();
    const input = el.querySelector('[data-testid="replace-input"]') as HTMLInputElement;
    const open = vi.spyOn(input, 'click').mockImplementation(() => undefined);
    (row(el, 'doc-1')!.querySelector('[data-action="replace"]') as HTMLElement).click();
    expect(open).toHaveBeenCalled();
    const file = fileOf('v4.pdf');
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(api.replacePolicyDocumentFile).toHaveBeenCalledWith({ id: 'doc-1', 'If-Match': '"7"', body: { file } });
  });

  it('a too large file chosen for Replace is refused in the browser', async () => {
    const { fixture, el } = await setup();
    const input = el.querySelector('[data-testid="replace-input"]') as HTMLInputElement;
    vi.spyOn(input, 'click').mockImplementation(() => undefined);
    (row(el, 'doc-1')!.querySelector('[data-action="replace"]') as HTMLElement).click();
    Object.defineProperty(input, 'files', { value: [fileOf('big.pdf', 10 * 1024 * 1024 + 1)], configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(api.replacePolicyDocumentFile).not.toHaveBeenCalled();
    expect(text(el.querySelector('[data-testid="action-error"]'))).toContain('larger than');
  });

  it('a file chosen in the drop zone is uploaded; the server message shows when it is refused', async () => {
    const { fixture, el } = await setup();
    api.uploadPolicyDocument.mockRejectedValueOnce(new HttpErrorResponse({ status: 413, error: { detail: 'The file is too large.' } }));
    const input = el.querySelector('[data-testid="upload"] input[type=file]') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [fileOf('a.pdf')], configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(api.uploadPolicyDocument).toHaveBeenCalled();
    expect(text(el.querySelector('[data-testid="action-error"]'))).toContain('The file is too large.');
  });

  it('page_hidden_without_admin_policies: the route guard sends a user without the permission to /forbidden', async () => {
    TestBed.configureTestingModule({ providers: providers([], ['assistant.use']) });
    const router = TestBed.inject(Router);
    router.resetConfig([{ path: 'app', children: APP_ROUTES }, { path: 'forbidden', children: [] }]);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/app/admin/policies');
    expect(router.url).toBe('/forbidden');
    expect(api.listPolicyDocuments).not.toHaveBeenCalled();
  });
});
