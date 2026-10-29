import { formatRand, keepRand, KeepRandPipe, RandPipe } from './money.pipe';

const intl = (n: number) =>
  new Intl.NumberFormat('en-ZA', { style: 'currency', currency: 'ZAR', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(n);
// Intl uses no-break and narrow spaces; compare the look with one plain space.
const plain = (s: string) => s.replace(/[\s  ]/g, ' ');

describe('formatRand', () => {
  it('formats 86940 as R 86 940,00 exactly as Intl en-ZA does (no-break spaces kept)', () => {
    expect(formatRand(86940)).toBe(intl(86940));
    expect(plain(formatRand(86940))).toBe('R 86 940,00');
    expect(formatRand(86940)).not.toContain(' '); // no ordinary space that could wrap
  });

  it('formats zero', () => {
    expect(plain(formatRand(0))).toBe('R 0,00');
  });

  it('formats negative amounts', () => {
    expect(formatRand(-1234.5)).toBe(intl(-1234.5));
    expect(plain(formatRand(-1234.5))).toMatch(/^-R 1 234,50$/);
  });

  it('keeps two decimals', () => {
    expect(plain(formatRand(0.1 + 0.2))).toBe('R 0,30');
  });
});

describe('RandPipe', () => {
  it('formats a number and shows nothing for null or undefined', () => {
    const pipe = new RandPipe();
    expect(pipe.transform(86940)).toBe(formatRand(86940));
    expect(pipe.transform(null)).toBe('');
    expect(pipe.transform(undefined)).toBe('');
  });
});

describe('keepRand', () => {
  it('keeps the R of every amount in a server sentence with its number', () => {
    const text = keepRand('R 86 940,00 reserved on IT-200, R 5,00 left (R is a letter)');
    expect(text).toBe('R\u00a086 940,00 reserved on IT-200, R\u00a05,00 left (R is a letter)');
  });

  it('leaves letters before an R alone (PR 12, DR 5)', () => {
    expect(keepRand('See PR 12 and DR 5, then R 5,00')).toBe('See PR 12 and DR 5, then R 5,00');
  });

  it('leaves letters before an R alone (PR 12, DR 5)', () => {
    expect(keepRand('See PR 12 and DR 5, then R 5,00')).toBe('See PR 12 and DR 5, then R 5,00');
  });

  it('leaves a sentence without amounts alone, and copes with nothing', () => {
    expect(keepRand('Approved by Maria')).toBe('Approved by Maria');
    expect(keepRand(null)).toBe('');
    expect(new KeepRandPipe().transform(undefined)).toBe('');
  });
});
