import { escalatesIn, waitedFor } from './inbox-time';

describe('inbox time', () => {
  const now = Date.parse('2026-10-28T12:00:00Z');
  const ago = (ms: number) => new Date(now - ms).toISOString();
  const ahead = (ms: number) => new Date(now + ms).toISOString();
  const H = 3_600_000;

  it('waitedFor uses one unit', () => {
    expect(waitedFor(ago(10_000), now)).toBe('just now');
    expect(waitedFor(ago(5 * 60_000), now)).toBe('5 min');
    expect(waitedFor(ago(2 * H + 1000), now)).toBe('2 h');
    expect(waitedFor(ago(26 * H), now)).toBe('1 d');
    expect(waitedFor(null, now)).toBe('');
    expect(waitedFor(ahead(5000), now)).toBe('just now'); // clock skew never shows a negative time
  });

  it('escalatesIn counts down and says now when due', () => {
    expect(escalatesIn(ahead(20 * 60_000), now)).toBe('in 20 min');
    expect(escalatesIn(ahead(6 * H + 5000), now)).toBe('in 6 h');
    expect(escalatesIn(ahead(28 * H), now)).toBe('in 1 d 4 h');
    expect(escalatesIn(ahead(48 * H), now)).toBe('in 2 d');
    expect(escalatesIn(ago(1000), now)).toBe('now');
    expect(escalatesIn(null, now)).toBe('');
  });
});
