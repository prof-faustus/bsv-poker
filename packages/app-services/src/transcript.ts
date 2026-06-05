/**
 * Reconnect / resume (core §8.6, §12.3; REQ-NET-007, REQ-DATA-002/003) — rebuild a hand's state
 * from the transcript (the ordered records on the indexer). A (re)connecting client fetches the
 * records and replays them through the deterministic engine to obtain byte-identical state; the
 * truth never depended on staying connected (P2/P3). Browser-safe.
 */

import {
  type Action,
  type GameState,
  type Ruleset,
  type Card,
  sha256,
  bytesToHex,
  tryHexToBytes,
  safeJsonParse,
  constantTimeEqualHex,
} from '@bsv-poker/protocol-types';
import { createGameModule } from './game-registry.ts';
import { deckFromEntropies } from './mp-shuffle.ts';
import type { TxRecord } from './network.ts';
import type { TablePlayer } from './interactive-client.ts';

interface TEnvelope {
  t: 'commit' | 'reveal' | 'action';
  seat: number;
  hand: number;
  c?: string;
  r?: string;
  kind?: Action['kind'];
  amount?: number;
  discard?: readonly number[];
}

/** Max base64 length for a single transcript record (one envelope) — bounds the atob/JSON work. */
const MAX_RECORD_B64 = 64 * 1024;

function parseRecords(records: readonly TxRecord[]): TEnvelope[] {
  const out: TEnvelope[] = [];
  for (const rec of records) {
    // Trust boundary: transcript records come from the (untrusted) indexer. Bound the base64
    // length, decode defensively, then bounded-parse the JSON — never trust a record blindly.
    if (!rec.raw || typeof rec.raw !== 'string' || rec.raw.length > MAX_RECORD_B64) continue;
    let json: string;
    try {
      json = atob(rec.raw);
    } catch {
      continue; // not valid base64
    }
    const parsed = safeJsonParse(json, { maxBytes: MAX_RECORD_B64, maxDepth: 6 });
    if (parsed.ok && parsed.value !== null && typeof parsed.value === 'object') {
      out.push(parsed.value as TEnvelope);
    }
  }
  return out;
}

/**
 * Rebuild the final state of hand `handNo` from the transcript records, verifying each reveal
 * against its commit. Returns the reconstructed state and its hash (must match the live clients').
 */
export function rebuildHand(
  records: readonly TxRecord[],
  ruleset: Ruleset,
  seats: readonly TablePlayer[],
  handNo = 0,
  buttonIndex = 0,
): { state: GameState; stateHash: string } {
  const envs = parseRecords(records).filter((e) => e.hand === handNo);
  const seatList = [...seats].sort((a, b) => a.seat - b.seat);

  const entropies: Uint8Array[] = seatList.map((s) => {
    const reveal = envs.find((e) => e.t === 'reveal' && e.seat === s.seat);
    if (!reveal?.r) throw new Error(`transcript missing reveal for seat ${s.seat}`);
    const bytes = tryHexToBytes(reveal.r);
    if (bytes === null) throw new Error(`transcript reveal is not valid hex for seat ${s.seat}`);
    const commit = envs.find((e) => e.t === 'commit' && e.seat === s.seat);
    if (commit?.c && !constantTimeEqualHex(bytesToHex(sha256(bytes)), commit.c)) {
      throw new Error(`transcript reveal does not match commit for seat ${s.seat}`);
    }
    return bytes;
  });

  const deck: Card[] = deckFromEntropies(entropies);
  const m = createGameModule(ruleset.variant, deck, buttonIndex);
  let state = m.init(ruleset, seatList.map((s) => ({ seat: s.seat, stack: s.stack })));

  // The canonical store interleaves the two paths, so raw record order is NOT guaranteed to be
  // turn order (§8.5). Each seat's OWN actions ARE in order, so replay by the engine's toAct:
  // at each step take the next unused action for the seat on the clock. Deterministic + robust.
  const queues = new Map<number, Action[]>();
  for (const e of envs) {
    if (e.t !== 'action') continue;
    const a: Action = { kind: e.kind!, seat: e.seat, amount: e.amount ?? 0, ...(e.discard ? { discard: e.discard } : {}) };
    (queues.get(e.seat) ?? queues.set(e.seat, []).get(e.seat)!).push(a);
  }
  const cursor = new Map<number, number>();
  for (let guard = 0; guard < 5000 && !state.handComplete; guard++) {
    const toAct = state.betting.toAct ?? state.drawToAct ?? null;
    if (toAct === null) break;
    const q = queues.get(toAct) ?? [];
    const i = cursor.get(toAct) ?? 0;
    if (i >= q.length) break; // transcript does not (yet) cover this seat's next move
    cursor.set(toAct, i + 1);
    state = m.apply(state, q[i]!);
  }
  return { state, stateHash: m.stateHash(state) };
}
