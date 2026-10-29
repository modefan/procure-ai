/**
 * A new `Idempotency-Key` for one submission of a write. A form or button keeps the key for as long as the same operation on
 * the same data is repeated (a double click, a retry), so the server replays the first result; changed data gets a new key.
 */
export const idempotencyKey = (): string => crypto.randomUUID();
