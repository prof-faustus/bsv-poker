/**
 * Interactive networked table client (app §A6.5/§A7) — the human-driven counterpart to
 * NetworkedTableClient. A real player joins a table, the entropy commit/reveal handshake runs
 * over the relay, the deck is derived from the agreed entropies, then the player acts on their
 * turn via submitAction() while peers' actions arrive over the channel. onUpdate fires on every
 * state change (your turn / peer acted / hand complete) so a UI can render it. Browser-safe.
 *
 * Two honest clients converge to byte-identical state (P2 / REQ-TEST-002); the relay is
 * transport-only (P3).
 */

import {
  type Action,
  type Card,
  type LegalActions,
  type Ruleset,
  sha256,
  bytesToHex,
  hexToBytes,
  ByteWriter,
} from '@bsv-poker/protocol-types';
import { createHoldem, type HoldemState } from '@bsv-poker/game-holdem';
import type { RelayClient } from './network.ts';

export interface TablePlayer {
  readonly seat: number;
  readonly stack: number;
}

interface Envelope {
  t: 'commit' | 'reveal' | 'action';
  seat: number;
  c?: string;
  r?: string;
  kind?: Action['kind'];
  amount?: number;
  discard?: readonly number[];
}

export interface ClientUpdate {
  readonly state: HoldemState;
  readonly mySeat: number;
  readonly yourTurn: boolean;
  readonly legal: LegalActions | null;
  readonly complete: boolean;
}

/** Deterministic seeded Fisher–Yates over portable sha256 (identical on every client). */
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

export class InteractiveNetworkedTableClient {
  private readonly relay: RelayClient;
  private readonly tableId: string;
  private readonly mySeat: number;
  private readonly seats: TablePlayer[];
  private readonly ruleset: Ruleset;
  private readonly entropy: Uint8Array;
  private readonly inbox: Envelope[] = [];
  private unsub: (() => void) | null = null;
  private listeners: Array<(u: ClientUpdate) => void> = [];
  private pendingAction: ((a: Action) => void) | null = null;
  private module: ReturnType<typeof createHoldem> | null = null;
  private state: HoldemState | null = null;

  constructor(opts: {
    relay: RelayClient;
    tableId: string;
    mySeat: number;
    seats: TablePlayer[];
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

  onUpdate(cb: (u: ClientUpdate) => void): () => void {
    this.listeners.push(cb);
    return () => {
      this.listeners = this.listeners.filter((x) => x !== cb);
    };
  }

  /** Submit the local player's action when it is their turn (no-op otherwise). */
  submitAction(a: Action): void {
    const p = this.pendingAction;
    if (p) {
      this.pendingAction = null;
      p(a);
    }
  }

  getState(): HoldemState | null {
    return this.state;
  }

  stateHash(): string | null {
    return this.module && this.state ? this.module.stateHash(this.state) : null;
  }

  legalActions(): LegalActions | null {
    if (!this.module || !this.state) return null;
    if (this.state.betting.toAct !== this.mySeat) return null;
    return this.module.getLegalActions(this.state, this.mySeat);
  }

  private emit(complete = false): void {
    if (!this.state || !this.module) return;
    const yourTurn = !complete && this.state.betting.toAct === this.mySeat;
    const update: ClientUpdate = {
      state: this.state,
      mySeat: this.mySeat,
      yourTurn,
      legal: yourTurn ? this.module.getLegalActions(this.state, this.mySeat) : null,
      complete,
    };
    for (const l of this.listeners) l(update);
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
  /**
   * Re-broadcast `envs` every 300ms until `done()`. CRITICAL: a peer who subscribed late must
   * still receive my earlier envelopes, so callers keep re-sending the commit during the reveal
   * phase too — a player must not stop broadcasting its commit just because IT has all commits.
   */
  private async gossip(envs: Envelope[], done: () => boolean, timeoutMs = 30000): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    for (const e of envs) await this.publish(e);
    while (!done()) {
      if (Date.now() > deadline) throw new Error('handshake timeout');
      await new Promise((r) => setTimeout(r, 300));
      if (!done()) for (const e of envs) await this.publish(e);
    }
  }

  /** Run the hand: subscribe, handshake, deal, then drive turns until the hand completes. */
  async play(): Promise<HoldemState> {
    this.unsub = this.relay.subscribe(this.tableId, (text) => {
      try {
        const env = JSON.parse(text) as Envelope;
        if (process.env.MP_DEBUG) console.error(`[icx seat${this.mySeat}] rx ${env.t} seat=${env.seat}`);
        this.inbox.push(env);
      } catch {
        /* keepalive */
      }
    });
    if (process.env.MP_DEBUG) console.error(`[icx seat${this.mySeat}] subscribed to ${this.tableId}`);
    try {
      const commitEnv: Envelope = { t: 'commit', seat: this.mySeat, c: bytesToHex(sha256(this.entropy)) };
      const revealEnv: Envelope = { t: 'reveal', seat: this.mySeat, r: bytesToHex(this.entropy) };
      const allCommits = (): boolean =>
        this.seats.every((s) => this.received((e) => e.t === 'commit' && e.seat === s.seat));
      const allReveals = (): boolean =>
        this.seats.every((s) => this.received((e) => e.t === 'reveal' && e.seat === s.seat));
      await this.gossip([commitEnv], allCommits);
      // keep re-sending the commit alongside the reveal so late-subscribing peers still get it
      await this.gossip([commitEnv, revealEnv], allReveals);
      const entropies: Uint8Array[] = [];
      for (const s of this.seats) {
        const commit = this.received((e) => e.t === 'commit' && e.seat === s.seat)!;
        const reveal = this.received((e) => e.t === 'reveal' && e.seat === s.seat)!;
        const r = hexToBytes(reveal.r!);
        if (bytesToHex(sha256(r)) !== commit.c) throw new Error(`bad reveal for seat ${s.seat}`);
        entropies.push(r);
      }
      const w = new ByteWriter();
      for (const e of entropies) for (const b of e) w.u8(b);
      const deck: Card[] = seededShuffle(sha256(w.toBytes()), 52);

      this.module = createHoldem({ deck });
      this.state = this.module.init(
        this.ruleset,
        this.seats.map((s) => ({ seat: s.seat, stack: s.stack })),
      );
      this.emit();

      const cursor = new Map<number, number>();
      while (!this.state.handComplete) {
        const toAct = this.state.betting.toAct;
        if (toAct === null) break;
        if (toAct === this.mySeat) {
          // Set the pending resolver BEFORE emitting, so a handler that calls submitAction()
          // synchronously inside onUpdate is not dropped.
          const action = await new Promise<Action>((res) => {
            this.pendingAction = res;
            this.emit(); // your turn (legal actions in the update)
          });
          this.state = this.module.apply(this.state, action);
          await this.publish({
            t: 'action',
            seat: this.mySeat,
            kind: action.kind,
            amount: action.amount,
            ...(action.discard ? { discard: action.discard } : {}),
          });
        } else {
          const seen = cursor.get(toAct) ?? 0;
          await this.awaitCond(() => this.peerActions(toAct).length > seen, 60000);
          const env = this.peerActions(toAct)[seen]!;
          cursor.set(toAct, seen + 1);
          this.state = this.module.apply(this.state, {
            kind: env.kind!,
            seat: toAct,
            amount: env.amount ?? 0,
            ...(env.discard ? { discard: env.discard } : {}),
          });
        }
        this.emit();
      }
      this.emit(true);
      return this.state;
    } finally {
      this.unsub?.();
    }
  }
}
