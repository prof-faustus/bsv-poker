/**
 * Persistence record contracts + validate-on-read (REQ-APP-131/132). The store holds tables,
 * players, transactions (the transcript), and card lineage. Every persisted record is validated on
 * READ; a corrupt record is **quarantined** (surfaced, never silently dropped or trusted), so a
 * storage fault can never feed bad load-bearing state into the engine/UI.
 */

export type RecordKind = 'table' | 'player' | 'transaction' | 'card-lineage';

export interface PersistedRecord {
  readonly kind: RecordKind;
  readonly id: string;
  readonly payload: Readonly<Record<string, unknown>>;
}

export type ReadResult<T> = { readonly ok: true; readonly value: T } | { readonly ok: false; readonly quarantined: unknown; readonly reason: string };

/** Validate a raw value read from the store against `isValid`; quarantine anything that fails. */
export function validateOnRead<T>(raw: unknown, isValid: (r: unknown) => r is T): ReadResult<T> {
  try {
    if (isValid(raw)) return { ok: true, value: raw };
    return { ok: false, quarantined: raw, reason: 'failed schema validation' };
  } catch (e) {
    return { ok: false, quarantined: raw, reason: `validation threw: ${(e as Error).message}` };
  }
}

/** Structural guard for a PersistedRecord (REQ-APP-132 validate-on-read). */
export function isPersistedRecord(r: unknown): r is PersistedRecord {
  if (!r || typeof r !== 'object') return false;
  const o = r as Record<string, unknown>;
  return (
    (o.kind === 'table' || o.kind === 'player' || o.kind === 'transaction' || o.kind === 'card-lineage') &&
    typeof o.id === 'string' && o.id.length > 0 &&
    !!o.payload && typeof o.payload === 'object'
  );
}

/**
 * Transcript retention (REQ-APP-133, a TRACKED ASSUMPTION): keep the most recent N hands. The
 * default is a documented assumption, overridable by configuration; older hands are pruned in order.
 */
export const DEFAULT_RETAINED_HANDS = 100;

export function applyRetention<T>(hands: readonly T[], keepN: number = DEFAULT_RETAINED_HANDS): T[] {
  if (keepN < 0) throw new Error('retention keepN must be >= 0');
  return keepN >= hands.length ? [...hands] : hands.slice(hands.length - keepN);
}

/** Read a batch, partitioning valid records from quarantined ones (no silent loss). */
export function readBatch(raws: readonly unknown[]): { records: PersistedRecord[]; quarantined: ReadResult<PersistedRecord>[] } {
  const records: PersistedRecord[] = [];
  const quarantined: ReadResult<PersistedRecord>[] = [];
  for (const raw of raws) {
    const r = validateOnRead(raw, isPersistedRecord);
    if (r.ok) records.push(r.value);
    else quarantined.push(r);
  }
  return { records, quarantined };
}
