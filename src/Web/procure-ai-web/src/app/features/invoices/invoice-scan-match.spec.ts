import { matchScannedLines, similarity } from './invoice-scan-match';
import { scanDto } from './invoice-scan.testing';

describe('matchScannedLines', () => {
  it('pairs by description, each scanned line once, and leaves unrelated ones out', () => {
    const scanned = scanDto().lines;
    const result = matchScannedLines(['Laptop 14"', 'Mouse', 'Standing desk'], scanned);
    expect(result[0]?.description).toBe('Laptop 14"');
    expect(result[1]?.description).toBe('Wireless mouse');
    expect(result[2]).toBeNull();
  });

  it('does not use one scanned line for two order lines', () => {
    const result = matchScannedLines(['Mouse', 'Mouse pad'], [{ description: 'Wireless mouse', quantity: 1, unitPriceExclVat: 1, lineTotalExclVat: 1 }]);
    expect(result.filter((r) => r !== null)).toHaveLength(1);
  });

  it('scores identical text 1 and unrelated text 0', () => {
    expect(similarity('Laptop 14"', 'laptop 14')).toBe(1);
    expect(similarity('Laptop', 'Chair')).toBe(0);
  });
});
