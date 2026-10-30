import { normaliseRequestNumber } from './access-check.store';

describe('normaliseRequestNumber', () => {
  it('accepts the PR- prefix with or without it, in any case', () => {
    expect(normaliseRequestNumber('1039')).toBe('PR-1039');
    expect(normaliseRequestNumber(' pr-1039 ')).toBe('PR-1039');
    expect(normaliseRequestNumber('PR-1039')).toBe('PR-1039');
  });

  it('returns null for an empty input', () => {
    expect(normaliseRequestNumber('   ')).toBeNull();
  });
});
