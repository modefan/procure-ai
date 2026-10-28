const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** How long ago `iso` was, in one unit: "just now", "5 min", "2 h", "1 d". Empty when unknown. */
export function waitedFor(iso: string | null | undefined, now: number): string {
  if (!iso) {
    return '';
  }
  const ms = Math.max(0, now - Date.parse(iso));
  if (ms < MINUTE) {
    return 'just now';
  }
  if (ms < HOUR) {
    return `${Math.floor(ms / MINUTE)} min`;
  }
  if (ms < DAY) {
    return `${Math.floor(ms / HOUR)} h`;
  }
  return `${Math.floor(ms / DAY)} d`;
}

/** When a step escalates, relative to now: "in 6 h", "in 1 d 4 h", "now" once it is due. Empty when it has no due time. */
export function escalatesIn(iso: string | null | undefined, now: number): string {
  if (!iso) {
    return '';
  }
  const ms = Date.parse(iso) - now;
  if (ms <= 0) {
    return 'now';
  }
  if (ms < HOUR) {
    return `in ${Math.max(1, Math.ceil(ms / MINUTE))} min`;
  }
  if (ms < DAY) {
    return `in ${Math.floor(ms / HOUR)} h`;
  }
  const days = Math.floor(ms / DAY);
  const hours = Math.floor((ms % DAY) / HOUR);
  return hours > 0 ? `in ${days} d ${hours} h` : `in ${days} d`;
}
