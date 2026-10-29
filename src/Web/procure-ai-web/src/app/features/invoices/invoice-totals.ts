import { VAT_RATE_PERCENT } from '../requests/request-lines';

export interface Totals {
  exclVat: number;
  vat: number;
  inclVat: number;
}

export interface PricedInput {
  quantity: number;
  unitPriceExclVat: number;
}

const cents = (rand: number) => Math.round(rand * 100);

/**
 * Totals of an invoice typed in by finance. Done in cents so sums never drift; VAT is worked out once on the total
 * (rounded to the cent, like the server), never per line. Lines without a usable number count as nothing.
 * The server's numbers are authoritative; this only previews them.
 */
export function invoiceTotals(lines: readonly PricedInput[]): Totals {
  const excl = lines.reduce((sum, l) => (Number.isFinite(l.quantity) && Number.isFinite(l.unitPriceExclVat) ? sum + Math.round(cents(l.unitPriceExclVat) * l.quantity) : sum), 0);
  const vat = Math.round((excl * VAT_RATE_PERCENT) / 100);
  return { exclVat: excl / 100, vat: vat / 100, inclVat: (excl + vat) / 100 };
}
