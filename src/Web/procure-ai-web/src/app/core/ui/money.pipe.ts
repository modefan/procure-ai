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

/**
 * The server writes amounts into its sentences as "R 96 600,00", with a plain space after the R. Swap that space for
 * a no-break one, so a sentence never wraps between the R and its number.
 */
export function keepRand(text: string | null | undefined): string {
  return (text ?? '').replace(/R (?=\d)/g, 'R\u00a0');
}

/** `{{ sentence | keepRand }}`: a server sentence with its amounts kept in one piece. */
@Pipe({ name: 'keepRand' })
export class KeepRandPipe implements PipeTransform {
  transform(text: string | null | undefined): string {
    return keepRand(text);
  }
}
