import { CatalogItemDto } from '../../api/suppliers/models/catalog-item-dto';
import { RequestLineDto } from '../../api/requests/models/request-line-dto';

/** Display VAT rate. The server's totals are authoritative; this only previews them while the form is edited. */
export const VAT_RATE_PERCENT = 15;
export const MAX_TITLE_LENGTH = 200;
export const MAX_QUANTITY = 10_000;

export interface LineInput {
  sku: string;
  quantity: number;
}

export interface PricedLine extends LineInput {
  description: string;
  /** Null when the SKU is not (yet) known. */
  unitPrice: number | null;
  total: number | null;
}

export interface Totals {
  exclVat: number;
  vat: number;
  inclVat: number;
}

const toCents = (rand: number) => Math.round(rand * 100);

/**
 * Looks each line up in the supplier catalog (else in the lines the saved request already holds, for a SKU the
 * catalog dropped since) to show description, unit price and line total. Done in cents so sums never drift.
 */
export function priceLines(lines: readonly LineInput[], catalog: readonly CatalogItemDto[], saved: readonly RequestLineDto[]): PricedLine[] {
  return lines.map((line) => {
    const found = line.sku === '' ? undefined : (catalog.find((c) => c.sku === line.sku) ?? saved.find((s) => s.sku === line.sku));
    if (!found) {
      return { ...line, description: '', unitPrice: null, total: null };
    }
    const total = Number.isFinite(line.quantity) ? (toCents(found.unitPriceExclVat) * line.quantity) / 100 : null;
    return { ...line, description: found.description, unitPrice: found.unitPriceExclVat, total };
  });
}

/** Excl. VAT is the sum of the lines; VAT is 15 % of that total (rounded once, to the cent); incl. VAT is both. */
export function computeTotals(lines: readonly PricedLine[]): Totals {
  const exclCents = lines.reduce((sum, l) => sum + (l.total === null ? 0 : toCents(l.total)), 0);
  const vatCents = Math.round((exclCents * VAT_RATE_PERCENT) / 100);
  return { exclVat: exclCents / 100, vat: vatCents / 100, inclVat: (exclCents + vatCents) / 100 };
}
