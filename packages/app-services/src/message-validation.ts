/**
 * Trust-boundary input validation (REQ-APP-103). Every message crossing a trust boundary — relay /
 * peer envelopes (and, on desktop, IPC) — is validated before use; anything unrecognized or
 * malformed is REJECTED (returns null), never partially trusted. This is the structural guard the
 * networked client applies to inbound envelopes from the (untrusted) relay channel.
 */

export type EnvelopeKind = 'commit' | 'reveal' | 'action';

export interface WireEnvelope {
  readonly t: EnvelopeKind;
  readonly seat: number;
  readonly hand: number;
  readonly c?: string; // commit: H(entropy) hex
  readonly r?: string; // reveal: entropy hex
  readonly kind?: string; // action: ActionKind
  readonly amount?: number; // action: optional wager
}

const isHex = (v: unknown): v is string => typeof v === 'string' && /^[0-9a-f]+$/i.test(v) && v.length > 0;
const isSeatOrHand = (v: unknown): v is number => typeof v === 'number' && Number.isInteger(v) && v >= 0;

/** Validate an inbound envelope; return the typed envelope or null if it must be rejected. */
export function validateEnvelope(raw: unknown): WireEnvelope | null {
  if (!raw || typeof raw !== 'object') return null;
  const o = raw as Record<string, unknown>;
  if (o.t !== 'commit' && o.t !== 'reveal' && o.t !== 'action') return null; // unrecognized → reject
  if (!isSeatOrHand(o.seat) || !isSeatOrHand(o.hand)) return null;

  if (o.t === 'commit') {
    if (!isHex(o.c)) return null;
    return { t: 'commit', seat: o.seat, hand: o.hand, c: o.c };
  }
  if (o.t === 'reveal') {
    if (!isHex(o.r)) return null;
    return { t: 'reveal', seat: o.seat, hand: o.hand, r: o.r };
  }
  // action
  if (typeof o.kind !== 'string' || o.kind.length === 0) return null;
  if (o.amount !== undefined && (typeof o.amount !== 'number' || !Number.isFinite(o.amount))) return null;
  const env: WireEnvelope = { t: 'action', seat: o.seat, hand: o.hand, kind: o.kind };
  return o.amount !== undefined ? { ...env, amount: o.amount } : env;
}

/** Parse a JSON wire frame and validate it; null on bad JSON or a rejected envelope. */
export function parseAndValidate(frame: string): WireEnvelope | null {
  try {
    return validateEnvelope(JSON.parse(frame));
  } catch {
    return null;
  }
}
