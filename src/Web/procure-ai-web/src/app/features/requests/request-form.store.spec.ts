import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsService } from '../../api/requests/services/requests.service';
import { ConflictDialog } from '../../core/ui/conflict-dialog';
import { RequestFormStore } from './request-form.store';

const dto = (o: Partial<RequestDto> = {}): RequestDto => ({
  id: 'r1', number: 'PR-1001', title: 'Laptops', status: 'Draft', costCenterCode: 'IT-200', supplierId: 'CH', requesterId: 'tom',
  lines: [], submittedAt: null, approvedAt: null, rejectedAt: null, rejectionReason: null, cancelledAt: null,
  cancellationReason: null, orderId: null, orderNumber: null, spentInclVat: null, spentInclVatDisplay: null, cancellationRefusedReason: null,
  totalExclVat: 75600, totalExclVatDisplay: 'R 75 600,00', vat: 11340, vatDisplay: 'R 11 340,00', totalInclVat: 86940, totalInclVatDisplay: 'R 86 940,00',
  version: 1, ...o,
});

const draft = { title: 'Laptops', supplierId: 'CH', costCenterCode: 'IT-200', lines: [{ sku: 'L14', quantity: 3 }] };

const problem = (status: number, body: object) => new HttpErrorResponse({ status, error: body });

describe('RequestFormStore', () => {
  let api: Record<'createRequest' | 'updateRequest' | 'submitRequest' | 'getRequest', ReturnType<typeof vi.fn>>;
  let show: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    api = {
      createRequest: vi.fn().mockResolvedValue(dto()),
      updateRequest: vi.fn().mockResolvedValue(dto({ version: 2 })),
      submitRequest: vi.fn().mockResolvedValue(dto({ status: 'Submitted', version: 3 })),
      getRequest: vi.fn().mockResolvedValue(dto()),
    };
    show = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      providers: [RequestFormStore, { provide: RequestsService, useValue: api }, { provide: ConflictDialog, useValue: { show } }],
    });
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
  });

  const store = () => TestBed.inject(RequestFormStore);

  it('saving a new draft creates it with an Idempotency-Key and keeps the saved request', async () => {
    const saved = await store().saveDraft(draft);

    expect(saved?.id).toBe('r1');
    expect(api.createRequest).toHaveBeenCalledOnce();
    const params = api.createRequest.mock.calls[0][0];
    expect(params['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);
    expect(params.body).toEqual({ title: 'Laptops', supplierId: 'CH', costCenterCode: 'IT-200', lines: [{ sku: 'L14', quantity: 3 }] });
    expect(store().request()?.id).toBe('r1');
  });

  it('saving an existing draft updates it with If-Match from the loaded version, then uses the new version', async () => {
    api.getRequest.mockResolvedValue(dto({ version: 4 }));
    await store().load('r1');

    await store().saveDraft({ ...draft, title: 'Laptops 2' });
    await store().saveDraft({ ...draft, title: 'Laptops 3' });

    expect(api.updateRequest.mock.calls[0][0]).toEqual({ id: 'r1', 'If-Match': '"4"', body: { title: 'Laptops 2', lines: [{ sku: 'L14', quantity: 3 }] } });
    expect(api.updateRequest.mock.calls[1][0]['If-Match']).toBe('"2"');
    expect(api.createRequest).not.toHaveBeenCalled();
  });

  it('submit_sends_one_idempotency_key_per_submission', async () => {
    const first = store().submit(draft);
    const second = store().submit(draft); // the double click
    await Promise.all([first, second]);

    expect(api.createRequest).toHaveBeenCalledOnce();
    expect(api.submitRequest).toHaveBeenCalledOnce();
    expect(api.submitRequest.mock.calls[0][0]).toMatchObject({ id: 'r1', 'If-Match': '"1"' });
    expect(api.submitRequest.mock.calls[0][0]['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('retrying the same submission after a failure repeats the key; a changed draft gets a new one', async () => {
    api.submitRequest.mockRejectedValueOnce(problem(503, { title: 'Busy' }));
    await store().submit(draft);
    await store().submit(draft);
    const [a, b] = api.submitRequest.mock.calls.map((c) => c[0]['Idempotency-Key']);
    expect(a).toBe(b);
    expect(api.createRequest).toHaveBeenCalledOnce(); // already saved: not created twice

    api.submitRequest.mockRejectedValueOnce(problem(503, { title: 'Busy' }));
    await store().submit({ ...draft, title: 'Changed' }); // saved first (new version), so a new submission
    expect(api.updateRequest).toHaveBeenCalledOnce();
    expect(api.submitRequest.mock.calls[2][0]['Idempotency-Key']).not.toBe(a);
  });

  it('saves pending edits first and submits the version the save returned', async () => {
    api.getRequest.mockResolvedValue(dto({ version: 4 }));
    await store().load('r1');

    const result = await store().submit({ ...draft, title: 'Edited' });

    expect(api.updateRequest).toHaveBeenCalledOnce();
    expect(api.submitRequest.mock.calls[0][0]['If-Match']).toBe('"2"');
    expect(result?.status).toBe('Submitted');
  });

  it('server_field_errors_are_kept_per_field, and 422 general errors become the alert text', async () => {
    api.createRequest.mockRejectedValueOnce(
      problem(400, {
        title: 'One or more validation errors occurred.',
        errors: { Title: ['Title is too long.'], 'Lines[1].Quantity': ['Quantity must be between 1 and 10000.'], '$.lines[0].sku': ['Unknown SKU.'], Lines: ['At least one line.'], SupplierId: ['Unknown supplier.'], CostCenterCode: ['Not your cost center.'] },
      }),
    );

    const result = await store().saveDraft(draft);

    expect(result).toBeNull();
    expect(store().fieldErrors()).toEqual({
      title: ['Title is too long.'],
      'lines.1.quantity': ['Quantity must be between 1 and 10000.'],
      'lines.0.sku': ['Unknown SKU.'],
      lines: ['At least one line.'],
      supplier: ['Unknown supplier.'],
      costCenter: ['Not your cost center.'],
    });
    expect(store().generalError()).toBeNull();

    api.createRequest.mockRejectedValueOnce(problem(422, { title: 'Unprocessable', detail: 'The supplier is not active.' }));
    await store().saveDraft(draft);
    expect(store().generalError()).toBe('The supplier is not active.');
    expect(store().fieldErrors()).toEqual({});
  });

  it('a stale version (412) shows the conflict dialog, then loads the latest version', async () => {
    api.getRequest.mockResolvedValueOnce(dto({ version: 1 }));
    await store().load('r1');
    api.updateRequest.mockRejectedValueOnce(problem(412, { detail: 'The request changed.' }));
    api.getRequest.mockResolvedValueOnce(dto({ version: 7 }));

    const result = await store().saveDraft({ ...draft, title: 'Mine' });

    expect(result).toBeNull();
    expect(show).toHaveBeenCalledOnce();
    expect(store().request()?.version).toBe(7);
    expect(store().isBusy()).toBe(false);
  });

  it('busy covers the whole call', async () => {
    let finish!: (v: RequestDto) => void;
    api.createRequest.mockReturnValueOnce(new Promise<RequestDto>((resolve) => (finish = resolve)));

    const saving = store().saveDraft(draft);
    expect(store().busy()).toBe('saving');

    finish(dto());
    await saving;
    expect(store().busy()).toBe('idle');
  });

  it('a failed load is an error, not an empty form', async () => {
    api.getRequest.mockRejectedValueOnce(problem(404, { title: 'Not found' }));

    await store().load('missing');

    expect(store().error()).toBe('Not found');
    expect(store().request()).toBeNull();
  });

  it('another operation while one runs is refused, not run in parallel; the same one shares the running call', async () => {
    let finish!: (v: RequestDto) => void;
    api.createRequest.mockReturnValueOnce(new Promise<RequestDto>((resolve) => (finish = resolve)));

    const saving = store().saveDraft(draft);
    const sameAgain = store().saveDraft(draft);
    const submitting = store().submit(draft);

    await expect(submitting).resolves.toBeNull();
    expect(api.submitRequest).not.toHaveBeenCalled();
    finish(dto());
    await expect(saving).resolves.toMatchObject({ id: 'r1' });
    await expect(sameAgain).resolves.toMatchObject({ id: 'r1' });
    expect(api.createRequest).toHaveBeenCalledOnce();
  });

  it('savedMatches says whether the server holds exactly what the form holds', async () => {
    expect(store().savedMatches(draft)).toBe(false); // nothing saved yet

    await store().saveDraft(draft);

    expect(store().savedMatches(draft)).toBe(true);
    expect(store().savedMatches({ ...draft, title: 'Other' })).toBe(false);
    expect(store().savedMatches({ ...draft, lines: [{ sku: 'L14', quantity: 4 }] })).toBe(false);
  });
});
