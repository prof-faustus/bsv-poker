/**
 * NetworkedTableClient (app §A7, core §8) — runs a REAL multiplayer hand over the relay: each
 * player exchanges its entropy commit/reveal (core §4.1) and its betting actions over the
 * table-scoped channel (Tier B), and derives state locally through the deterministic engine.
 * Two honest clients given the same valid action set converge to byte-identical state
 * (cross-client agreement, P2 / REQ-TEST-002). The relay stays transport-only (REQ-NET-001).
 *
 * Browser-safe: uses the portable sha256 (no node:crypto). The distributed shuffle here is the
 * commit-reveal composition over the agreed entropies (INV-CT-1); the on-chain per-card crypto
 * (combined keys, fair-play) is the SDK/Node path.
 */

import {
  type Action,
  type LegalActions,
  type Ruleset,
  type Card,
  sha256,
  bytesToHex,
  hexToBytes,
  ByteWriter,
  safeJsonParse,
  constantTimeEqualHex,
} from '@bsv-poker/protocol-types';
import { createHoldem, type HoldemState } from '@bsv-poker/game-holdem';
import type { RelayClient } from './network.ts';
import { validateEnvelope } from './message-validation.ts';

export interface NetworkedSeat {
  readonly seat: number;
  readonly stack: number;
}

export type Strategy = (legal: LegalActions, seat: number, state: HoldemState) => Action;

interface Envelope {
  t: 'commit' | 'reveal' | 'action';
  seat: number;
  c?: string;
  r?: string;
  kind?: Action['kind'];
  amount?: number;
}

/** Deterministic seeded Fisher–Yates (counter-mode portable sha256) — identical on every client. */
function seededShuffle(seed: Uint8Array, n: number): number[] {
  const perm = Array.from({ length: n }, (_, i) => i);
  let counter = 0;
  let pool: number[] = [];
  const draw = (): number => {
    if (pool.length === 0) {
      const w = new ByteWriter();
      for (const b of seed) w.u8(b);
      w.u32(counter++);
      const h = sha256(w.toBytes());
      for (let i = 0; i + 4 <= h.length; i += 4) {
        pool.push(((h[i]! << 24) | (h[i + 1]! << 16) | (h[i + 2]! << 8) | h[i + 3]!) >>> 0);
      }
    }
    return pool.shift()!;
  };
  for (let i = n - 1; i > 0; i--) {
    const j = draw() % (i + 1);
    [perm[i], perm[j]] = [perm[j]!, perm[i]!];
  }
  return perm;
}

export class NetworkedTableClient {
  private readonly relay: RelayClient;
  private readonly tableId: string;
  private readonly mySeat: number;
  private readonly seats: NetworkedSeat[];
  private readonly ruleset: Ruleset;
  private readonly entropy: Uint8Array;
  private readonly inbox: Envelope[] = [];
  private unsub: (() => void) | null = null;

  constructor(opts: {
    relay: RelayClient;
    tableId: string;
    mySeat: number;
    seats: NetworkedSeat[];
    ruleset: Ruleset;
    entropy: Uint8Array;
  }) {
    this.relay = opts.relay;
    this.tableId = opts.tableId;
    this.mySeat = opts.mySeat;
    this.seats = [...opts.seats].sort((a, b) => a.seat - b.seat);
    this.ruleset = opts.ruleset;
    this.entropy = opts.entropy;
  }

  private async publish(env: Envelope): Promise<void> {
    await this.relay.publish(this.tableId, new TextEncoder().encode(JSON.stringify(env)));
  }

  private received(pred: (e: Envelope) => boolean): Envelope | undefined {
    return this.inbox.find(pred);
  }

  private peerActions(seat: number): Envelope[] {
    return this.inbox.filter((e) => e.t === 'action' && e.seat === seat);
  }

  private async awaitCond(done: () => boolean, timeoutMs: number): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (!done()) {
      if (Date.now() > deadline) throw new Error('timeout waiting for a table message');
      await new Promise((r) => setTimeout(r, 25));
    }
  }

  /** Run a full hand to completion; returns the final state hash (for convergence checks). */
  async runHand(strategy: Strategy): Promise<{ state: HoldemState; stateHash: string }> {
    this.unsub = this.relay.subscribe(this.tableId, (text) => {
      // Trust boundary: every inbound relay frame is bounded-parsed AND structurally validated
      // before it enters the inbox; malformed/oversize/unknown frames are dropped, never trusted
      // (CWE-400 + REQ-APP-103). hexToBytes(reveal.r) downstream is then guaranteed valid hex.
      const parsed = safeJsonParse(text, { maxBytes: 16384, maxDepth: 6 });
      if (!parsed.ok) return;
      const env = validateEnvelope(parsed.value);
      if (env !== null) this.inbox.push(env as Envelope);
    });
    try {
      // 1) entropy commit/reveal handshake. Keep re-sending the commit during the reveal phase
      //    too, so a peer that subscribed late still receives it (a player must not stop
      //    broadcasting its commit just because IT already has everyone's).
      const commitEnv: Envelope = { t: 'commit', seat: this.mySeat, c: bytesToHex(sha256(this.entropy)) };
      const revealEnv: Envelope = { t: 'reveal', seat: this.mySeat, r: bytesToHex(this.entropy) };
      const allCommits = (): boolean =>
        this.seats.every((s) => this.received((e) => e.t === 'commit' && e.seat === s.seat));
      const allReveals = (): boolean =>
        this.seats.every((s) => this.received((e) => e.t === 'reveal' && e.seat === s.seat));
      await this.gossip([commitEnv], allCommits);
      await this.gossip([commitEnv, revealEnv], allReveals);

      // 2) verify reveals against commits, derive the shared deck (seat-ordered entropies).
      const entropies: Uint8Array[] = [];
      for (const s of this.seats) {
        const commit = this.received((e) => e.t === 'commit' && e.seat === s.seat)!;
        const reveal = this.received((e) => e.t === 'reveal' && e.seat === s.seat)!;
        const r = hexToBytes(reveal.r!); // validated as hex at the trust boundary above
        if (!constantTimeEqualHex(bytesToHex(sha256(r)), commit.c!)) throw new Error(`bad reveal for seat ${s.seat}`);
        entropies.push(r);
      }
      const w = new ByteWriter();
      for (const e of entropies) for (const b of e) w.u8(b);
      const deck: Card[] = seededShuffle(sha256(w.toBytes()), 52);

      // 3) play: act on my turn (apply + publish); apply peers' actions from the channel in
      //    arrival order (one cursor per peer seat).
      const m = createHoldem({ deck });
      let state = m.init(this.ruleset, this.seats.map((s) => ({ seat: s.seat, stack: s.stack })));
      const cursor = new Map<number, number>(); // peer seat -> # of its actions already applied
      while (!state.handComplete) {
        const toAct = state.betting.toAct;
        if (toAct === null) break; // engine auto-advanced into a terminal/non-betting state
        if (toAct === this.mySeat) {
          const legal = m.getLegalActions(state, this.mySeat);
          const action = strategy(legal, this.mySeat, state);
          state = m.apply(state, action);
          await this.publish({ t: 'action', seat: this.mySeat, kind: action.kind, amount: action.amount });
        } else {
          const seen = cursor.get(toAct) ?? 0;
          await this.awaitCond(() => this.peerActions(toAct).length > seen, 10000);
          const env = this.peerActions(toAct)[seen]!;
          cursor.set(toAct, seen + 1);
          state = m.apply(state, { kind: env.kind!, seat: toAct, amount: env.amount ?? 0 });
        }
      }
      return { state, stateHash: m.stateHash(state) };
    } finally {
      this.unsub?.();
    }
  }

  /** Publish `env` and re-publish every 300ms until `done()` (robust to the subscribe join race). */
  private async gossip(envs: Envelope[], done: () => boolean, timeoutMs = 30000): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    for (const e of envs) await this.publish(e);
    while (!done()) {
      if (Date.now() > deadline) throw new Error('handshake timeout');
      await new Promise((r) => setTimeout(r, 300));
      if (!done()) for (const e of envs) await this.publish(e);
    }
  }
}

