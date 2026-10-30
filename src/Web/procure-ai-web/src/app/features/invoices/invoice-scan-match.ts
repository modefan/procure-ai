import { ScannedLineDto } from '../../api/assistant/models/scanned-line-dto';

/** Words of a description: lower case letters and digits, plural "s" dropped from longer words. */
function words(text: string): Set<string> {
  const found = text
    .toLowerCase()
    .split(/[^\p{L}\p{N}]+/u)
    .filter((w) => w !== '')
    .map((w) => (w.length > 3 && w.endsWith('s') ? w.slice(0, -1) : w));
  return new Set(found);
}

/**
 * How alike two descriptions are, 0 to 1: shared words over the words of the shorter one ("Wireless mouse" and "Mouse"
 * share all of the shorter's words, so 1), blended with the shared words over all words so a longer unrelated text scores lower.
 */
export function similarity(a: string, b: string): number {
  const x = words(a);
  const y = words(b);
  if (x.size === 0 || y.size === 0) {
    return 0;
  }
  let shared = 0;
  for (const w of x) {
    if (y.has(w)) {
      shared++;
    }
  }
  const contained = shared / Math.min(x.size, y.size);
  const jaccard = shared / (x.size + y.size - shared);
  return 0.7 * contained + 0.3 * jaccard;
}

const THRESHOLD = 0.5;

/**
 * Pairs each order line with the scanned line that describes it best (each scanned line is used once; the best pairs
 * first). Returns, per order line description in the given order, the matched scanned line or null.
 */
export function matchScannedLines(orderDescriptions: string[], scanned: ScannedLineDto[]): (ScannedLineDto | null)[] {
  const pairs: { order: number; scan: number; score: number }[] = [];
  orderDescriptions.forEach((description, order) =>
    scanned.forEach((line, scan) => {
      const score = similarity(description, line.description);
      if (score >= THRESHOLD) {
        pairs.push({ order, scan, score });
      }
    }),
  );
  pairs.sort((p, q) => q.score - p.score || p.order - q.order || p.scan - q.scan);
  const result: (ScannedLineDto | null)[] = orderDescriptions.map(() => null);
  const used = new Set<number>();
  for (const pair of pairs) {
    if (result[pair.order] === null && !used.has(pair.scan)) {
      result[pair.order] = scanned[pair.scan];
      used.add(pair.scan);
    }
  }
  return result;
}
