import { HttpErrorResponse } from '@angular/common/http';

export interface ParsedProblem {
  /** The ProblemDetails `detail` (else `title`) when the error is not about specific fields. */
  general: string | null;
  /** Field errors by form path, see `RequestFormStore`. */
  fields: Record<string, string[]>;
}

/** Server property names (any casing, with or without a leading `$.`) to form field paths; the request form's names are mapped, any other key stays as sent (lower case). */
function pathOf(serverKey: string): string {
  const key = serverKey.replace(/^\$\.?/, '').toLowerCase();
  const line = /^lines\[(\d+)\]\.(sku|quantity)$/.exec(key);
  if (line) {
    return `lines.${line[1]}.${line[2]}`;
  }
  switch (key) {
    case 'title':
      return 'title';
    case 'supplierid':
      return 'supplier';
    case 'costcentercode':
      return 'costCenter';
    case 'lines':
      return 'lines';
    default:
      return key;
  }
}

/**
 * Reads a failed call's ProblemDetails. Validation errors (`errors`) become field errors; for those the alert stays
 * empty. Anything else (422, 403, ...) gives `general`: the body's `detail`, else `title`. Never raw exception text.
 */
export function problemOf(error: unknown): ParsedProblem {
  const empty: ParsedProblem = { general: null, fields: {} };
  if (!(error instanceof HttpErrorResponse) || !error.error || typeof error.error !== 'object') {
    return empty;
  }
  const body = error.error as { detail?: unknown; title?: unknown; errors?: unknown };
  if (body.errors && typeof body.errors === 'object') {
    const fields: Record<string, string[]> = {};
    for (const [key, messages] of Object.entries(body.errors as Record<string, unknown>)) {
      if (Array.isArray(messages)) {
        const path = pathOf(key);
        fields[path] = [...(fields[path] ?? []), ...messages.filter((m): m is string => typeof m === 'string')];
      }
    }
    if (Object.keys(fields).length > 0) {
      return { general: null, fields };
    }
  }
  const text = [body.detail, body.title].find((t): t is string => typeof t === 'string' && t.trim() !== '');
  return { general: text ?? null, fields: {} };
}

/**
 * True when a write lost to someone else: 412 (the `If-Match` version is old) or 409 (the thing is not in the state the write
 * needs any more). Stores answer both the same way: the conflict dialog, then the latest version.
 */
export function isConflict(error: unknown): boolean {
  return error instanceof HttpErrorResponse && (error.status === 412 || error.status === 409);
}
