import { fiscalYearLabel } from './fiscal-year';

describe('fiscalYearLabel', () => {
  it('writes the two calendar years the fiscal year spans', () => {
    expect(fiscalYearLabel('FY2027')).toBe('FY 2026/27');
    expect(fiscalYearLabel('FY2100')).toBe('FY 2099/00');
  });

  it('leaves anything else as the server sent it', () => {
    expect(fiscalYearLabel('2026')).toBe('2026');
    expect(fiscalYearLabel('')).toBe('');
  });
});
