/**
 * "FY2027" (the fiscal year is named after the year it ends in, March to February) as "FY 2026/27", the two calendar years it
 * spans. Anything that is not of that shape is shown as the server sent it.
 */
export function fiscalYearLabel(fiscalYear: string): string {
  const match = /^FY(\d{4})$/.exec(fiscalYear);
  if (!match) {
    return fiscalYear;
  }
  const end = Number(match[1]);
  return `FY ${end - 1}/${String(end % 100).padStart(2, '0')}`;
}
