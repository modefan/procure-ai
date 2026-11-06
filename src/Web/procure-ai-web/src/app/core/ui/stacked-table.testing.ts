/**
 * A table that stacks into cards below `lg` (`display: block` on the table, its row groups, rows and cells) loses its table
 * semantics in Chrome and Safari: screen readers no longer announce it as a table. Explicit ARIA roles keep them, at every width.
 */
export function expectExplicitTableRoles(table: HTMLTableElement | null): void {
  expect(table, 'table').not.toBeNull();
  expect(table!.getAttribute('role')).toBe('table');
  for (const group of table!.querySelectorAll(':scope > thead, :scope > tbody')) {
    expect(group.getAttribute('role'), group.tagName).toBe('rowgroup');
  }
  const rows = table!.querySelectorAll('tr');
  expect(rows.length).toBeGreaterThan(1);
  for (const row of rows) {
    expect(row.getAttribute('role'), row.outerHTML.slice(0, 80)).toBe('row');
  }
  for (const header of table!.querySelectorAll('thead th')) {
    expect(header.getAttribute('role'), header.textContent ?? '').toBe('columnheader');
  }
  for (const cell of table!.querySelectorAll('tbody td')) {
    expect(cell.getAttribute('role'), cell.textContent ?? '').toBe('cell');
  }
}
