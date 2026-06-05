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
  type GameState,
  type LegalActions,
  type Ruleset,
  sha256,
  bytesToHex,
  hexToBytes,
  ByteWriter,
  safeJsonParse,
  constantTimeEqualHex,
} from '@bsv-poker/protocol-types';
import type { RelayClient, IndexerClient } from './network.ts';
import { createGameModule, type GenericGameModule } from './game-registry.ts';
import { deckFromEntropies } from './mp-shuffle.ts';
import { seatedForNextHand } from './table-participants.ts';
import { type SessionAuth, verifySig, envelopeMessage } from './session-auth.ts';
import { validateEnvelope } from './message-validation.ts';

export interface TablePlayer {
  readonly seat: number;
  readonly stack: number;
}

interface Envelope {
  t: 'commit' | 'reveal' | 'action';
  seat: number;
  /** Hand index within the session — separates each hand's commits/reveals/actions. */
  hand: number;
  c?: string;
  r?: string;
  kind?: Action['kind'];
  amount?: number;
  discard?: readonly number[];
  /** Prior state hash the action acts on — bound into the signature (audit 8). */
  prev?: string;
  /** Ed25519 signature by the seat's session key over envelopeMessage(tableId, env) (audit 1–3). */
  sig?: string;
}

export interface ClientUpdate {
  readonly state: GameState;
  readonly mySeat: number;
  readonly yourTurn: boolean;
  readonly legal: LegalActions | null;
  readonly complete: boolean;
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
  private module: GenericGameModule | null = null;
  private state: GameState | null = null;
  private aborted = false;
  private ingestSeq = 0; // makes each ingested record's txid unique (identical checks would collide)
  private registered = false; // indexer seat→pub registration attempted (audit 7, validating mode)
  private readonly indexer: IndexerClient | null;
  private readonly auth: SessionAuth | null;
  private readonly seatPubs: readonly string[] | null;

  constructor(opts: {
    relay: RelayClient;
    tableId: string;
    mySeat: number;
    seats: TablePlayer[];
    ruleset: Ruleset;
    entropy: Uint8Array;
    /** Optional canonical path: every envelope is also ingested here for transcript/reconnect. */
    indexer?: IndexerClient;
    /** This seat's session signing key — signs every envelope this client emits (audit 1–3). */
    auth?: SessionAuth;
    /** seat → registered session pubkey. Inbound envelopes MUST be signed by the acting seat's key
     *  or they are rejected (no forging another seat's action). */
    seatPubs?: readonly string[];
    /** TEST FIXTURES ONLY (audit 1): permit unsigned play. Production live play must NOT set this. */
    allowUnsigned?: boolean;
  }) {
    // Live multiplayer requires authentication: a signing key to emit and the seat→key map to verify
    // inbound. Unsigned mode is permitted only behind an explicit test flag (audit 1).
    if (!opts.allowUnsigned && (!opts.auth || !opts.seatPubs)) {
      throw new Error('live network play requires `auth` + `seatPubs` (audit 1); set allowUnsigned only in test fixtures');
    }
    this.relay = opts.relay;
    this.tableId = opts.tableId;
    this.mySeat = opts.mySeat;
    this.seats = [...opts.seats].sort((a, b) => a.seat - b.seat);
    this.ruleset = opts.ruleset;
    this.entropy = opts.entropy;
    this.indexer = opts.indexer ?? null;
    this.auth = opts.auth ?? null;
    this.seatPubs = opts.seatPubs ?? null;
  }

  onUpdate(cb: (u: ClientUpdate) => void): () => void {
    this.listeners.push(cb);
    return () => {
      this.listeners = this.listeners.filter((x) => x !== cb);
    };
  }

  /** Stop a running session (e.g. the player leaves the table); the current hand finishes. */
  abort(): void {
    this.aborted = true;
  }

  /** Submit the local player's action when it is their turn (no-op otherwise). */
  submitAction(a: Action): void {
    const p = this.pendingAction;
    if (p) {
      this.pendingAction = null;
      p(a);
    }
  }

  getState(): GameState | null {
    return this.state;
  }

  stateHash(): string | null {
    return this.module && this.state ? this.module.stateHash(this.state) : null;
  }

  /** Seat to act: a betting turn, else a non-betting decision turn (e.g. Draw's discard). */
  private static toAct(s: GameState): number | null {
    return s.betting.toAct ?? s.drawToAct ?? null;
  }

  legalActions(): LegalActions | null {
    if (!this.module || !this.state) return null;
    if (InteractiveNetworkedTableClient.toAct(this.state) !== this.mySeat) return null;
    return this.module.getLegalActions(this.state, this.mySeat);
  }

  private emit(complete = false): void {
    if (!this.state || !this.module) return;
    const yourTurn = !complete && InteractiveNetworkedTableClient.toAct(this.state) === this.mySeat;
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
    // Sign every envelope with this seat's session key so peers can prove I sent it (audit 1–3).
    if (this.auth && !env.sig) env.sig = await this.auth.sign(envelopeMessage(this.tableId, env));
    const json = JSON.stringify(env);
    // speed path: relay channel
    await this.relay.publish(this.tableId, new TextEncoder().encode(json));
    // transcript REPLAY LOG (audit 4): the indexer stores signed envelopes for reconnect/rebuild. It
    // is NOT a validated canonical transaction graph — the record id is a local content hash, not a
    // protocol txid, so this path must not be described as canonical until it ingests validated,
    // signed, branch-bound protocol transactions. The relay channel carries the live signed envelopes.
    if (this.indexer) {
      // unique per occurrence: identical envelopes (e.g. repeated checks) must NOT dedup away
      const recordId = bytesToHex(sha256(new TextEncoder().encode(`${this.mySeat}:${this.ingestSeq++}:${json}`)));
      try {
        await this.indexer.ingest({ txid: recordId, class: env.t, tableId: this.tableId, raw: btoa(json) });
      } catch {
        /* replay-log best-effort; the relay channel carries the live signed envelopes this session */
      }
    }
  }
  private received(pred: (e: Envelope) => boolean): Envelope | undefined {
    return this.inbox.find(pred);
  }
  private peerActions(seat: number, hand: number): Envelope[] {
    return this.inbox.filter((e) => e.t === 'action' && e.seat === seat && e.hand === hand);
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

  /**
   * Register this table's seat→pubkey map with the indexer ONCE (audit 7). In the indexer's
   * validating mode this authorises the seats whose envelopes it will authenticate; against an
   * opaque indexer it is harmlessly ignored. Best-effort: the relay channel is the live truth, the
   * indexer is the (now authenticated) transcript, so a registration failure does not stop play.
   */
  private async registerSeatsOnce(): Promise<void> {
    if (this.registered || !this.indexer || !this.seatPubs) return;
    this.registered = true; // attempt once regardless of outcome (avoid a retry storm)
    const seats = this.seatPubs
      .map((pub, seat) => ({ seat, pub }))
      .filter((s) => typeof s.pub === 'string' && s.pub.length > 0);
    if (seats.length === 0) return;
    try {
      await this.indexer.register(this.tableId, seats);
    } catch {
      /* indexer is best-effort transcript; live play continues over the relay */
    }
  }

  private subscribe(): void {
    if (this.unsub) return;
    void this.registerSeatsOnce();
    this.unsub = this.relay.subscribe(this.tableId, (text) => {
      // Validate at the trust boundary (audit 6), then verify the signature is by the key REGISTERED
      // to the acting seat (audit 1–3) before accepting. All async, so do it off the callback.
      void (async () => {
        // Bounded parse of the untrusted relay frame BEFORE any work (CWE-400); then structural
        // validation; then signature verification. Fail closed at every step.
        const parsed = safeJsonParse(text, { maxBytes: 16384, maxDepth: 6 });
        if (!parsed.ok) return; // oversize / malformed / keepalive
        const raw: unknown = parsed.value;
        const env = validateEnvelope(raw); // structural validation (parseAndValidate layer)
        if (!env) return; // malformed → reject
        if (this.seatPubs) {
          const pub = this.seatPubs[env.seat];
          const sig = (raw as { sig?: string }).sig;
          if (!pub || !sig || !(await verifySig(pub, envelopeMessage(this.tableId, env as Envelope), sig))) {
            if (process.env.MP_DEBUG) console.error(`[icx seat${this.mySeat}] REJECTED unsigned/forged ${env.t} seat=${env.seat}`);
            return; // unsigned, wrong-seat, or forged → reject
          }
        }
        if (process.env.MP_DEBUG) console.error(`[icx seat${this.mySeat}] rx ${env.t} h${env.hand} seat=${env.seat}`);
        this.inbox.push(env as Envelope);
      })();
    });
  }

  /** Run ONE hand at `handNo` over `seats` with `buttonIndex`, using `entropy` for the shuffle. */
  private async playOneHand(
    handNo: number,
    seats: TablePlayer[],
    buttonIndex: number,
    entropy: Uint8Array,
  ): Promise<GameState> {
    const commitEnv: Envelope = { t: 'commit', seat: this.mySeat, hand: handNo, c: bytesToHex(sha256(entropy)) };
    const revealEnv: Envelope = { t: 'reveal', seat: this.mySeat, hand: handNo, r: bytesToHex(entropy) };
    const has = (t: 'commit' | 'reveal', seat: number): boolean =>
      !!this.received((e) => e.t === t && e.seat === seat && e.hand === handNo);
    await this.gossip([commitEnv], () => seats.every((s) => has('commit', s.seat)));
    await this.gossip([commitEnv, revealEnv], () => seats.every((s) => has('reveal', s.seat)));

    const entropies: Uint8Array[] = [];
    for (const s of seats) {
      const commit = this.received((e) => e.t === 'commit' && e.seat === s.seat && e.hand === handNo)!;
      const reveal = this.received((e) => e.t === 'reveal' && e.seat === s.seat && e.hand === handNo)!;
      const r = hexToBytes(reveal.r!); // validated as hex at the trust boundary in subscribe()
      if (!constantTimeEqualHex(bytesToHex(sha256(r)), commit.c!)) throw new Error(`bad reveal for seat ${s.seat}`);
      entropies.push(r);
    }
    const deck: Card[] = deckFromEntropies(entropies);

    this.module = createGameModule(this.ruleset.variant, deck, buttonIndex);
    this.state = this.module.init(this.ruleset, seats.map((s) => ({ seat: s.seat, stack: s.stack })));
    this.emit();

    const cursor = new Map<number, number>();
    while (!this.state.handComplete) {
      const toAct = InteractiveNetworkedTableClient.toAct(this.state);
      if (toAct === null) break;
      if (toAct === this.mySeat) {
        const action = await new Promise<Action>((res) => {
          this.pendingAction = res;
          this.emit(); // your turn (legal actions in the update)
        });
        const prev = this.module.stateHash(this.state); // state hash BEFORE the action (audit 8)
        this.state = this.module.apply(this.state, action);
        await this.publish({
          t: 'action',
          seat: this.mySeat,
          hand: handNo,
          kind: action.kind,
          amount: action.amount,
          prev,
          ...(action.discard ? { discard: action.discard } : {}),
        });
      } else {
        const seen = cursor.get(toAct) ?? 0;
        await this.awaitCond(() => this.peerActions(toAct, handNo).length > seen, 120000);
        const env = this.peerActions(toAct, handNo)[seen]!;
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
  }

  /** Single hand (subscribe + one hand at index 0, button 0). */
  async play(): Promise<GameState> {
    this.subscribe();
    try {
      return await this.playOneHand(0, this.seats, 0, this.entropy);
    } finally {
      this.unsub?.();
      this.unsub = null;
    }
  }

  /**
   * Continuous table: play hand after hand — fresh per-hand entropy (REQ-CRYPTO-010), carried
   * stacks, rotating button — until `maxHands`, only one player has chips, or this player busts.
   * onUpdate fires throughout (state.handNumber distinguishes hands).
   */
  async playSession(opts?: { maxHands?: number }): Promise<void> {
    // Provable upper bound on the session loop (NASA P10). A real session terminates far earlier
    // via the in-loop conditions (bust, <2 players, abort); this ceiling guarantees termination.
    const HARD_SESSION_CEILING = 1_000_000;
    const requested = opts?.maxHands ?? HARD_SESSION_CEILING;
    const maxHands = Number.isFinite(requested) ? Math.min(requested, HARD_SESSION_CEILING) : HARD_SESSION_CEILING;
    this.subscribe();
    // running stacks per ORIGINAL seat number, carried hand to hand
    const stacks = new Map<number, number>(this.seats.map((s) => [s.seat, s.stack]));
    let button = 0;
    try {
      for (let hand = 0; hand < maxHands; hand++) {
        if (this.aborted) break;
        // Participant set is (re)computed between hands and frozen for this hand (REQ-CRYPTO-011).
        const participants = seatedForNextHand(this.seats, (seat) => stacks.get(seat) ?? 0);
        if (participants.length < 2) break; // table can't continue
        if (!participants.some((p) => p.seat === this.mySeat)) break; // I busted → I'm out
        const seats: TablePlayer[] = participants.map((s) => ({ seat: s.seat, stack: stacks.get(s.seat)! }));
        const buttonIndex = button % seats.length;
        // fresh, per-hand entropy bound to the hand index (a new N-party shuffle each hand)
        const handEntropy = sha256(concat(this.entropy, u32(hand)));
        const final = await this.playOneHand(hand, seats, buttonIndex, handEntropy);
        for (const s of final.seats) stacks.set(s.seat, s.stack);
        button += 1;
      }
    } finally {
      this.unsub?.();
      this.unsub = null;
    }
  }
}

function concat(a: Uint8Array, b: Uint8Array): Uint8Array {
  const out = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}
function u32(n: number): Uint8Array {
  const w = new ByteWriter();
  w.u32(n);
  return w.toBytes();
}
