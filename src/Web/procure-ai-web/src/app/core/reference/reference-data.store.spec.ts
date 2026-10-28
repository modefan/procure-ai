import { TestBed } from '@angular/core/testing';
import { AccessService } from '../../api/access/services/access.service';
import { SuppliersService } from '../../api/suppliers/services/suppliers.service';
import { CacheRegistry } from '../state/cache-registry';
import { ReferenceDataStore } from './reference-data.store';

const me = {
  userId: 'u1',
  name: 'Tom',
  roles: ['requester'],
  approvalLimit: null,
  costCenters: [{ code: 'CC-100', name: 'Marketing', departmentCode: 'D1' }],
};

describe('ReferenceDataStore', () => {
  let getMe: ReturnType<typeof vi.fn>;
  let listSuppliers: ReturnType<typeof vi.fn>;
  let searchCatalog: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    getMe = vi.fn().mockResolvedValue(me);
    listSuppliers = vi.fn().mockResolvedValue({ items: [{ code: 'S1', name: 'Acme Supplies', active: true, itemCount: 2 }], page: 1, pageSize: 100, total: 1 });
    searchCatalog = vi.fn().mockResolvedValue({ items: [{ sku: 'A1' }], page: 1, pageSize: 100, total: 1 });
    TestBed.configureTestingModule({
      providers: [
        { provide: AccessService, useValue: { getMe } },
        { provide: SuppliersService, useValue: { listSuppliers, searchCatalog } },
      ],
    });
  });

  const store = () => TestBed.inject(ReferenceDataStore);

  it('loadMe reads access/me once for the whole session and derives the cost centers', async () => {
    await store().loadMe();
    await store().loadMe();

    expect(getMe).toHaveBeenCalledTimes(1);
    expect(store().me()?.name).toBe('Tom');
    expect(store().costCenters().map((c) => c.code)).toEqual(['CC-100']);
  });

  it('loadSuppliers reads every page', async () => {
    listSuppliers
      .mockResolvedValueOnce({ items: [{ code: 'S1', name: 'A', active: true, itemCount: 1 }], page: 1, pageSize: 1, total: 2 })
      .mockResolvedValueOnce({ items: [{ code: 'S2', name: 'B', active: true, itemCount: 1 }], page: 2, pageSize: 1, total: 2 });

    await store().loadSuppliers();

    expect(store().suppliers().map((s) => s.code)).toEqual(['S1', 'S2']);
    expect(listSuppliers).toHaveBeenCalledTimes(2);
  });

  it('catalogItems(code) is empty until loaded, then holds the supplier\'s items; one call per supplier', async () => {
    expect(store().catalogItems('S1')).toEqual([]);

    await store().loadCatalogItems('S1');
    await store().loadCatalogItems('S1');

    expect(searchCatalog).toHaveBeenCalledTimes(1);
    expect(store().catalogItems('S1').map((i) => i.sku)).toEqual(['A1']);
    expect(store().catalogItems('S2')).toEqual([]);
  });

  it('a failed load shows an error and a later call tries again', async () => {
    getMe.mockRejectedValueOnce(new Error('down'));
    await store().loadMe();
    expect(store().error()).not.toBeNull();
    expect(store().me()).toBeNull();

    await store().loadMe();

    expect(store().me()?.name).toBe('Tom');
    expect(store().error()).toBeNull();
  });

  it('is emptied by the registry on logout', async () => {
    await store().loadMe();

    TestBed.inject(CacheRegistry).clearAll();

    expect(store().me()).toBeNull();
    expect(store().suppliers()).toEqual([]);
  });
});
