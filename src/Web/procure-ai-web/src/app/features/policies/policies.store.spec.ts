import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { PolicyDocumentsService } from '../../api/assistant/services/policy-documents.service';
import { fakeHubFactory } from '../../core/live/fake-hub.testing';
import { LIVE_HUB_FACTORY } from '../../core/live/hub-connection';
import { LiveConnection } from '../../core/live/live-connection';
import { ConfirmDialog } from '../../core/ui/confirm-dialog';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { PoliciesStore } from './policies.store';
import { fileOf, policyDto } from './policies.testing';

describe('PoliciesStore', () => {
  let api: Record<'listPolicyDocuments' | 'uploadPolicyDocument' | 'replacePolicyDocumentFile' | 'removePolicyDocument', ReturnType<typeof vi.fn>>;
  let confirm: { ask: ReturnType<typeof vi.fn> };
  let conflict: { show: ReturnType<typeof vi.fn> };
  let fake: ReturnType<typeof fakeHubFactory>;
  const page = (items = [policyDto()]) => ({ items, page: 1, pageSize: 100, total: items.length });

  beforeEach(() => {
    api = {
      listPolicyDocuments: vi.fn().mockResolvedValue(page()),
      uploadPolicyDocument: vi.fn().mockResolvedValue(policyDto('doc-2', { status: 'Uploaded', progress: 0, chunkCount: 0, version: 1, currentVersion: 0 })),
      replacePolicyDocumentFile: vi.fn().mockResolvedValue(policyDto('doc-1', { version: 4, status: 'Uploaded', progress: 0, rowVersion: 8 })),
      removePolicyDocument: vi.fn().mockResolvedValue(undefined),
    };
    confirm = { ask: vi.fn().mockResolvedValue(true) };
    conflict = { show: vi.fn().mockResolvedValue(undefined) };
    fake = fakeHubFactory();
    TestBed.configureTestingModule({
      providers: [
        { provide: PolicyDocumentsService, useValue: api },
        { provide: ConfirmDialog, useValue: confirm },
        { provide: ConflictDialog, useValue: conflict },
        { provide: LIVE_HUB_FACTORY, useValue: fake.factory },
      ],
    });
  });
  const store = () => TestBed.inject(PoliciesStore);

  it('loads the documents through the generated client', async () => {
    await store().load();
    expect(store().entities().map((d) => d.id)).toEqual(['doc-1']);
    expect(store().isLoaded()).toBe(true);
  });

  it('progress_updates_on_entity_changed: a policy-document push re-reads the list', async () => {
    await store().load();
    api.listPolicyDocuments.mockResolvedValue(page([policyDto('doc-1', { status: 'Embedding', progress: 40, chunkCount: 0 })]));
    const live = TestBed.inject(LiveConnection);
    live.start();
    await vi.waitFor(() => expect(live.state()).toBe('connected'));
    fake.hubs[0].emit('EntityChanged', { entity: 'policy-document', id: 'doc-1', requestId: 'r1', eventType: 'PolicyDocumentProgress', occurredAt: '2026-10-29T10:00:00Z' });
    await vi.waitFor(() => expect(store().entityMap()['doc-1'].progress).toBe(40));
    expect(store().entityMap()['doc-1'].status).toBe('Embedding');
    live.stop();
  });

  it('upload sends the file with a fresh Idempotency-Key and shows the new document', async () => {
    await store().load();
    const file = fileOf('code.pdf');
    expect(await store().upload(file)).toBe(true);
    const args = api.uploadPolicyDocument.mock.calls[0][0];
    expect(args.body.file).toBe(file);
    expect(args['Idempotency-Key']).toMatch(/[0-9a-f-]{36}/);
    expect(store().entityMap()['doc-2'].status).toBe('Uploaded');
    expect(store().uploading()).toBe(false);
  });

  it('replace_sends_if_match: the loaded rowVersion goes in If-Match', async () => {
    await store().load();
    const file = fileOf('v4.md', 5, 'text/markdown');
    expect(await store().replace('doc-1', file)).toBe(true);
    expect(api.replacePolicyDocumentFile).toHaveBeenCalledWith({ id: 'doc-1', 'If-Match': '"7"', body: { file } });
    expect(store().entityMap()['doc-1'].version).toBe(4);
  });

  it('replace_412_opens_the_conflict_dialog_and_reloads', async () => {
    await store().load();
    api.replacePolicyDocumentFile.mockRejectedValue(new HttpErrorResponse({ status: 412 }));
    expect(await store().replace('doc-1', fileOf('v4.pdf'))).toBe(false);
    expect(conflict.show).toHaveBeenCalled();
    expect(api.listPolicyDocuments).toHaveBeenCalledTimes(2);
  });

  it('remove_asks_then_deletes: nothing is sent when the admin says no', async () => {
    await store().load();
    confirm.ask.mockResolvedValueOnce(false);
    expect(await store().remove('doc-1')).toBe(false);
    expect(api.removePolicyDocument).not.toHaveBeenCalled();

    expect(await store().remove('doc-1')).toBe(true);
    expect(confirm.ask).toHaveBeenCalledTimes(2);
    expect(confirm.ask.mock.calls[1][0].title).toContain('Procurement Policy 2026');
    expect(api.removePolicyDocument).toHaveBeenCalledWith({ id: 'doc-1', 'If-Match': '"7"' });
    expect(store().entities()).toHaveLength(0);
  });

  it('a refused upload leaves the server message in actionError', async () => {
    await store().load();
    api.uploadPolicyDocument.mockRejectedValue(new HttpErrorResponse({ status: 415, error: { title: 'Unsupported', detail: 'Only PDF or Markdown files are accepted.' } }));
    expect(await store().upload(fileOf('x.exe'))).toBe(false);
    expect(store().actionError()).toBe('Only PDF or Markdown files are accepted.');
  });
});
