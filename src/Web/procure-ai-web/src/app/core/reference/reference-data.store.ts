import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { CostCenterDto } from '../../api/access/models/cost-center-dto';
import { MeDto } from '../../api/access/models/me-dto';
import { AccessService } from '../../api/access/services/access.service';
import { CatalogItemDto } from '../../api/suppliers/models/catalog-item-dto';
import { SupplierSummaryDto } from '../../api/suppliers/models/supplier-summary-dto';
import { SuppliersService } from '../../api/suppliers/services/suppliers.service';
import { withCache } from '../state/with-cache';
import { withRequestStatus } from '../state/with-request-status';

interface ReferenceState {
  me: MeDto | null;
  suppliers: SupplierSummaryDto[];
  /** Catalog items by supplier code, for the suppliers loaded so far. */
  catalog: Record<string, CatalogItemDto[]>;
}

const PAGE_SIZE = 100;
const NO_ITEMS: CatalogItemDto[] = [];

/**
 * Reference data for forms and the dashboard: the signed-in user (`access/me`: name, roles, cost centers, approval
 * limit), the supplier list and each supplier's catalog. Cached for the whole session (`staleMs: Infinity`), emptied
 * on logout. Call a `load...` method before using the data; each is cheap when already loaded.
 */
export const ReferenceDataStore = signalStore(
  { providedIn: 'root' },
  withState<ReferenceState>({ me: null, suppliers: [], catalog: {} }),
  withRequestStatus(),
  withComputed(({ me }) => ({
    /** The cost centers the user may raise requests against. */
    costCenters: computed<CostCenterDto[]>(() => me()?.costCenters ?? []),
  })),
  withCache({ staleMs: Infinity }),
  withMethods((store) => {
    const access = inject(AccessService);
    const suppliersApi = inject(SuppliersService);

    /** Runs a cached load and records a failure in `error` instead of throwing at the screen. */
    const guarded = async (key: string, loader: () => Promise<void>) => {
      try {
        await store.cacheLoad(key, loader);
        store.setLoaded();
      } catch (error) {
        store.setError(error, 'Reference data could not be loaded.');
      }
    };

    return {
      loadMe: () =>
        guarded('me', async () => {
          const epoch = store.cacheEpoch();
          const me = await access.getMe();
          if (epoch === store.cacheEpoch()) {
            patchState(store, { me });
          }
        }),

      loadSuppliers: () =>
        guarded('suppliers', async () => {
          const epoch = store.cacheEpoch();
          const all: SupplierSummaryDto[] = [];
          for (let page = 1; ; page++) {
            const result = await suppliersApi.listSuppliers({ page, pageSize: PAGE_SIZE });
            all.push(...result.items);
            if (result.items.length === 0 || all.length >= result.total) {
              break;
            }
          }
          if (epoch === store.cacheEpoch()) {
            patchState(store, { suppliers: all });
          }
        }),

      /** Loads the whole catalog of one supplier. */
      loadCatalogItems: (supplierCode: string) =>
        guarded(`catalog:${supplierCode}`, async () => {
          const epoch = store.cacheEpoch();
          const all: CatalogItemDto[] = [];
          for (let page = 1; ; page++) {
            const result = await suppliersApi.searchCatalog({ code: supplierCode, page, pageSize: PAGE_SIZE });
            all.push(...result.items);
            if (result.items.length === 0 || all.length >= result.total) {
              break;
            }
          }
          if (epoch === store.cacheEpoch()) {
            patchState(store, (s) => ({ catalog: { ...s.catalog, [supplierCode]: all } }));
          }
        }),

      /** The catalog items of a supplier; empty until `loadCatalogItems` has run. Reactive when read in a template or computed. */
      catalogItems: (supplierCode: string): CatalogItemDto[] => store.catalog()[supplierCode] ?? NO_ITEMS,
    };
  }),
);
