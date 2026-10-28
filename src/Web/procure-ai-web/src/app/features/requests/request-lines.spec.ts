import { CatalogItemDto } from '../../api/suppliers/models/catalog-item-dto';
import { computeTotals, priceLines } from './request-lines';

const item = (sku: string, price: number, description = `Item ${sku}`): CatalogItemDto => ({
  sku, description, unit: 'each', active: true, unitPriceExclVat: price, unitPriceDisplay: `R ${price}`,
});

describe('request lines', () => {
  it('totals_and_vat_on_document_total', () => {
    const priced = priceLines([{ sku: 'L14', quantity: 3 }], [item('L14', 25200)], []);

    expect(priced[0].total).toBe(75600);
    expect(computeTotals(priced)).toEqual({ exclVat: 75600, vat: 11340, inclVat: 86940 });
  });

  it('works in cents so awkward prices do not drift', () => {
    const priced = priceLines([{ sku: 'A', quantity: 3 }, { sku: 'B', quantity: 1 }], [item('A', 0.1), item('B', 0.2)], []);

    expect(computeTotals(priced)).toEqual({ exclVat: 0.5, vat: 0.08, inclVat: 0.58 });
  });

  it('prices a SKU the catalog no longer lists from the saved line, and leaves a completely unknown SKU unpriced', () => {
    const saved = [{ lineNo: 1, sku: 'OLD', description: 'Old thing', quantity: 1, unitPriceExclVat: 10, totalExclVat: 10, unitPriceExclVatDisplay: '', totalExclVatDisplay: '' }];

    const priced = priceLines([{ sku: 'OLD', quantity: 2 }, { sku: 'NOPE', quantity: 1 }, { sku: '', quantity: 1 }], [], saved);

    expect(priced.map((l) => [l.description, l.unitPrice, l.total])).toEqual([['Old thing', 10, 20], ['', null, null], ['', null, null]]);
    expect(computeTotals(priced).exclVat).toBe(20);
  });

  it('ignores a non-numeric quantity', () => {
    const priced = priceLines([{ sku: 'A', quantity: Number.NaN }], [item('A', 5)], []);

    expect(priced[0].total).toBeNull();
  });
});
