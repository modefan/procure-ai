import { Pipe, PipeTransform } from '@angular/core';

const RAND = new Intl.NumberFormat('en-ZA', { style: 'currency', currency: 'ZAR', minimumFractionDigits: 2, maximumFractionDigits: 2 });

/**
 * `R 86 940,00`: Rand in the en-ZA style. The spaces are the no-break spaces Intl produces, kept on purpose so an
 * amount never wraps. For amounts the server already formatted (`...Display` fields) show those; use this for
 * numbers the UI computes or edits.
 */
export function formatRand(amount: number): string {
  return RAND.format(amount);
}

@Pipe({ name: 'rand' })
export class RandPipe implements PipeTransform {
  transform(amount: number | null | undefined): string {
    return amount === null || amount === undefined ? '' : formatRand(amount);
  }
}
