import { TestBed } from '@angular/core/testing';
import { RequestsService } from '../../api/requests/services/requests.service';
import { PagedResultOfRequestDto } from '../../api/requests/models/paged-result-of-request-dto';
import { RequestDto } from '../../api/requests/models/request-dto';
import { RequestsStore } from './requests.store';

function dto(n: number, overrides: Partial<RequestDto> = {}): RequestDto {
  return {
    id: `id-${n}`,
    number: `PR-${1000 + n}`,
    title: `Request ${n}`,
    status: 'Draft',
    costCenterCode: 'CC-100',
    supplierId: 's1',
    lines: [],
    submittedAt: null,
    totalExclVat: 100,
    totalExclVatDisplay: 'R 100,00',
    vat: 15,
    vatDisplay: 'R 15,00',
    totalInclVat: 115,
    totalInclVatDisplay: 'R 115,00',
    version: 1,
    ...overrides,
  };
}

const page = (items: RequestDto[], p: number, total: number): PagedResultOfRequestDto => ({ items, page: p, pageSize: 20, total });

describe('RequestsStore', () => {
  let listRequests: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    listRequests = vi.fn();
    TestBed.configureTestingModule({ providers: [{ provide: RequestsService, useValue: { listRequests } }] });
  });

  const store = () => TestBed.inject(RequestsStore);

  it('load sets entities and total from the generated client', async () => {
    listRequests.mockResolvedValue(page([dto(1), dto(2), dto(3)], 1, 3));

    await store().load(1);

    expect(listRequests).toHaveBeenCalledWith({ page: 1, pageSize: 20 });
    expect(store().entities().map((r) => r.number)).toEqual(['PR-1001', 'PR-1002', 'PR-1003']);
    expect(store().total()).toBe(3);
    expect(store().page()).toBe(1);
    expect(store().loading()).toBe(false);
    expect(store().error()).toBeNull();
  });

  it('shows the loading state while the call runs', async () => {
    let resolve!: (value: PagedResultOfRequestDto) => void;
    listRequests.mockReturnValue(new Promise((r) => (resolve = r)));

    const loading = store().load(1);
    expect(store().loading()).toBe(true);
    resolve(page([], 1, 0));
    await loading;

    expect(store().loading()).toBe(false);
  });

  it('replaces the entities when another page is loaded and derives the page count', async () => {
    listRequests.mockResolvedValueOnce(page([dto(1)], 1, 41));
    listRequests.mockResolvedValueOnce(page([dto(21)], 3, 41));

    await store().load(1);
    expect(store().pageCount()).toBe(3);
    await store().load(3);

    expect(listRequests).toHaveBeenLastCalledWith({ page: 3, pageSize: 20 });
    expect(store().entities().map((r) => r.number)).toEqual(['PR-1021']);
    expect(store().page()).toBe(3);
  });

  it('accepts numbers sent as strings (the generated model allows both)', async () => {
    listRequests.mockResolvedValue({ items: [dto(1)], page: '2', pageSize: '20', total: '21' });

    await store().load(2);

    expect(store().page()).toBe(2);
    expect(store().total()).toBe(21);
  });

  it('keeps the rows it has and reports an error when the call fails', async () => {
    listRequests.mockResolvedValueOnce(page([dto(1)], 1, 1));
    await store().load(1);
    listRequests.mockRejectedValueOnce(new Error('boom'));

    await store().load(1);

    expect(store().error()).toBe('The requests could not be loaded.');
    expect(store().loading()).toBe(false);
    expect(store().entities()).toHaveLength(1);
  });

  it('ignores the answer of an older call when a newer one was started', async () => {
    let resolveOld!: (value: PagedResultOfRequestDto) => void;
    listRequests.mockReturnValueOnce(new Promise((r) => (resolveOld = r)));
    listRequests.mockResolvedValueOnce(page([dto(21)], 2, 40));

    const old = store().load(1);
    await store().load(2);
    resolveOld(page([dto(1)], 1, 40));
    await old;

    expect(store().page()).toBe(2);
    expect(store().entities().map((r) => r.number)).toEqual(['PR-1021']);
  });
});
