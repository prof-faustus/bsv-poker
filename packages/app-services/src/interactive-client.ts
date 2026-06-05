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

/**
 * Browser-safe debug flag. This module is loaded in the browser bundle (no `process` global) AND in
 * Node. Reading `process.env` directly throws ReferenceError in the browser, so we reach `process`
 * through `globalThis` (typed locally, no @types/node needed) and capture the flag once. `false` in
 * any environment without a Node `process` — the in-tree browser build ships no `process` shim.
 */
const NODE_PROCESS = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process;
const MP_DEBUG = Boolean(NODE_PROCESS?.env?.MP_DEBUG);

export interface TablePlayer {
  readonly seat: number;
  readonly stack: number;
}

interface Envelope {
  t: 'commit' | 'reveal' | 'action' | 'timeout-claim';
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
  /** Anchored deadline block height of a timeout-claim (audit 3). */
  d?: number;
  /** Anchored block height a reveal/action was emitted at — the floor a timeout deadline clears (audit 3). */
  h?: number;
  /** timeout-claim: the seat being dropped (signed BY `seat`, the claimant, ABOUT `subject`) (audit 3). */
  subject?: number;
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
  // Accountable action timeout (audit 3): a shared, monotone chain-height observation is the agreed
  // clock. When a seat fails to act by an anchored deadline (floor + window blocks), any peer drops it
  // with the engine's check-or-fold default via a signed timeout-claim. Disabled when no heightSource
  // is supplied (e.g. a pure in-process test that never stalls) — the loop then waits unbounded-on-relay.
  private readonly heightSource: (() => Promise<number>) | null;
  private readonly timeoutWindow: number;
  // The greatest timeout deadline already APPLIED this hand. A drop advances the transcript's logical
  // clock to its deadline, so the NEXT turn's floor is at least that height and an honest seat is not
  // instantly timed out just because earlier waits (e.g. dropping a prior stalled seat) burned blocks.
  // Deterministic across clients: the deadline is floor+window, computed identically from the transcript.
  private timeoutFloorAdvance = 0;

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
    /** Shared chain-height observation — the agreed clock for the accountable action timeout (audit 3).
     *  All seated clients MUST read the same chain (e.g. the embedded node's tip) so the deadline is
     *  reached at the same height everywhere. Omit to disable the timeout (the loop waits on the relay). */
    heightSource?: () => Promise<number>;
    /** Blocks past the per-turn floor after which an unacted seat may be dropped (audit 3). Default 3. */
    timeoutWindow?: number;
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
    this.heightSource = opts.heightSource ?? null;
    if (opts.timeoutWindow !== undefined && (!Number.isInteger(opts.timeoutWindow) || opts.timeoutWindow < 1)) {
      throw new Error('timeoutWindow must be a positive integer number of blocks');
    }
    this.timeoutWindow = opts.timeoutWindow ?? 3;
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
  /**
   * Collect every seat's commit+reveal for the hand, DROPPING a non-responder past the anchored
   * deadline (audit 3 — the commit/reveal handshake half), and return the ACTIVE (non-dropped) seats.
   *
   * WHY a drop here needs more than the action-phase default: a missing reveal means the deck CANNOT
   * be derived — it is the composition of every party's secret permutation, so a withheld permutation
   * is not a move that has a "default", it is an absent input. The only way to continue is to EXCLUDE
   * the non-responder and re-derive the deck among the survivors (done by the caller from the returned
   * active set). The non-responder forfeits its bond on-chain (`bondRevealOrForfeitLocking`).
   *
   * WHY it is deterministic (no fork): the drop is driven by the SAME anchored-height + signed
   * `timeout-claim` mechanism as the action phase. The reveal deadline is `startHeight + window`; both
   * honest clients read the same chain height and see the same signed claims, so they compute the
   * IDENTICAL active set (and therefore the identical re-derived deck). With no `heightSource` the
   * timeout is disabled and we wait for everyone with a bounded fallback (the prior fail-closed
   * behaviour) — a missing player simply aborts the hand (correct for heads-up, where a survivor set
   * of one cannot form a hand).
   *
   * Re-broadcast discipline: a late-subscribing peer must still receive my earlier commit, so we keep
   * re-publishing both envelopes each tick until the active set is complete.
   */
  private async collectHandshake(
    seats: TablePlayer[],
    handNo: number,
    commitEnv: Envelope,
    revealEnv: Envelope,
    startHeight: number | undefined,
  ): Promise<TablePlayer[]> {
    const has = (t: 'commit' | 'reveal', seat: number): boolean =>
      !!this.received((e) => e.t === t && e.seat === seat && e.hand === handNo);
    const dropped = new Set<number>();
    const claimsSent = new Set<number>();
    const fallback = Date.now() + 120000;
    const window = this.timeoutWindow;
    const active = (): TablePlayer[] => seats.filter((s) => !dropped.has(s.seat));

    // Drop every active seat that has not produced `t` once the shared height passes the deadline,
    // via the SAME signed-timeout-claim mechanism as the action phase (deterministic across clients).
    const dropOverdue = async (t: 'commit' | 'reveal', deadline: number, h: number): Promise<void> => {
      for (const s of seats) {
        if (dropped.has(s.seat) || has(t, s.seat)) continue;
        const claim = this.inbox.find(
          (e) => e.t === 'timeout-claim' && e.subject === s.seat && e.hand === handNo && typeof e.d === 'number' && e.d >= deadline && h >= e.d,
        );
        if (claim) {
          dropped.add(s.seat);
          this.timeoutFloorAdvance = Math.max(this.timeoutFloorAdvance, deadline);
          continue;
        }
        if (h >= deadline && s.seat !== this.mySeat && !claimsSent.has(s.seat)) {
          claimsSent.add(s.seat);
          await this.publish({ t: 'timeout-claim', seat: this.mySeat, hand: handNo, subject: s.seat, d: deadline });
          dropped.add(s.seat);
          this.timeoutFloorAdvance = Math.max(this.timeoutFloorAdvance, deadline);
        }
      }
    };

    // PHASE 1 — COMMITS ONLY. A player must NOT reveal its entropy until it has seen every other active
    // seat's COMMIT; revealing earlier would let a seat that withholds its commit observe a reveal and
    // choose late entropy (core §4.1 / audit-02 #11). So we broadcast ONLY our commit here.
    for (;;) {
      if (this.aborted) throw new Error('client aborted during the handshake');
      await this.publish(commitEnv);
      if (active().every((s) => has('commit', s.seat))) break;
      const h = await this.nowHeight();
      if (h !== undefined && startHeight !== undefined) await dropOverdue('commit', startHeight + window, h);
      else if (Date.now() > fallback) throw new Error('handshake timeout');
      await new Promise((r) => setTimeout(r, 50));
    }

    // PHASE 2 — REVEALS. Now that every active seat has committed (entropy is bound), broadcasting the
    // reveal is safe. Keep re-publishing the commit too, for any late subscriber.
    for (;;) {
      if (this.aborted) throw new Error('client aborted during the handshake');
      await this.publish(commitEnv);
      await this.publish(revealEnv);
      if (active().every((s) => has('reveal', s.seat))) return active();
      const h = await this.nowHeight();
      if (h !== undefined && startHeight !== undefined) await dropOverdue('reveal', startHeight + window, h);
      else if (Date.now() > fallback) throw new Error('handshake timeout');
      await new Promise((r) => setTimeout(r, 50));
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
            if (MP_DEBUG) console.error(`[icx seat${this.mySeat}] REJECTED unsigned/forged ${env.t} seat=${env.seat}`);
            return; // unsigned, wrong-seat, or forged → reject
          }
        }
        if (MP_DEBUG) console.error(`[icx seat${this.mySeat}] rx ${env.t} h${env.hand} seat=${env.seat}`);
        this.inbox.push(env as Envelope);
      })();
    });
  }

  /** Current shared chain height, or undefined when the timeout is disabled (no heightSource). */
  private async nowHeight(): Promise<number | undefined> {
    if (!this.heightSource) return undefined;
    const h = await this.heightSource();
    return Number.isFinite(h) && h >= 0 ? Math.floor(h) : 0;
  }

  /**
   * The timeout FLOOR for `handNo`: the greatest anchored height bound into any reveal or action seen
   * this hand. A deadline must clear floor + window, so each turn (which raises the floor with its own
   * `h`) gets a fresh window and an honest-but-slow seat cannot be dropped before the window elapses.
   */
  private handFloor(handNo: number): number {
    let f = this.timeoutFloorAdvance; // a prior drop this hand moved the logical clock forward
    for (const e of this.inbox) {
      if (e.hand === handNo && (e.t === 'reveal' || e.t === 'action') && typeof e.h === 'number' && e.h > f) f = e.h;
    }
    return f;
  }

  /**
   * The engine's default move for an unresponsive seat (audit 3): check when checking is free, else
   * fold; on a draw decision, stand pat. Replayable from the legal-action set, so every client applies
   * the identical default and converges (P2).
   */
  private defaultAction(seat: number): Action {
    const legal = this.module!.getLegalActions(this.state!, seat);
    if (legal.draw) return { kind: 'stand', seat, amount: 0 };
    if (legal.check) return { kind: 'check', seat, amount: 0 };
    return { kind: 'fold', seat, amount: 0 };
  }

  /**
   * Wait for `seat`'s next action (index `seen`) OR for the anchored deadline to pass so the seat is
   * dropped with the default (audit 3). Returns the peer's action envelope, or 'timeout' to drop.
   *
   * Convergence rests on the anchored-height model: height is a slow, shared clock (block tips) while
   * the relay propagates an in-time action in milliseconds, so the window (blocks) is vastly larger
   * than propagation — no client reaches "deadline passed AND action absent" while another holds the
   * action. We also broadcast our own signed timeout-claim once matured so peers converge on the drop.
   * With no heightSource the timeout is disabled and we wait on the relay (bounded fallback).
   */
  private async awaitPeerActionOrTimeout(seat: number, handNo: number, seen: number): Promise<Envelope | 'timeout'> {
    const floor = this.handFloor(handNo);
    const deadline = floor + this.timeoutWindow;
    const fallback = Date.now() + 120000;
    let claimSent = false;
    for (;;) {
      if (this.aborted) throw new Error('client aborted while waiting for a peer');
      const acts = this.peerActions(seat, handNo);
      if (acts.length > seen) return acts[seen]!; // in-time action wins (fast path)
      const h = await this.nowHeight();
      if (h !== undefined) {
        // Accept a peer's signed, matured timeout-claim (verified in subscribe()) whose deadline clears
        // the agreed floor — a forged/premature claim (d < floor+window) or one not yet matured is ignored.
        const claim = this.inbox.find(
          (e) => e.t === 'timeout-claim' && e.subject === seat && e.hand === handNo && typeof e.d === 'number' && e.d >= deadline && h >= e.d,
        );
        if (claim) {
          // Advance by our own deterministic deadline (not the claim's possibly-larger d) so every
          // client moves the clock to the identical height for this drop.
          this.timeoutFloorAdvance = Math.max(this.timeoutFloorAdvance, deadline);
          return 'timeout';
        }
        if (!claimSent && h >= deadline && seat !== this.mySeat) {
          claimSent = true;
          await this.publish({ t: 'timeout-claim', seat: this.mySeat, hand: handNo, subject: seat, d: deadline });
          this.timeoutFloorAdvance = Math.max(this.timeoutFloorAdvance, deadline);
          return 'timeout';
        }
      } else if (Date.now() > fallback) {
        throw new Error('timeout waiting for a table message');
      }
      await new Promise((r) => setTimeout(r, 25));
    }
  }

  /** Run ONE hand at `handNo` over `seats` with `buttonIndex`, using `entropy` for the shuffle. */
  private async playOneHand(
    handNo: number,
    seats: TablePlayer[],
    buttonIndex: number,
    entropy: Uint8Array,
  ): Promise<GameState> {
    this.timeoutFloorAdvance = 0; // fresh logical clock per hand (audit 3)
    const commitEnv: Envelope = { t: 'commit', seat: this.mySeat, hand: handNo, c: bytesToHex(sha256(entropy)) };
    // Anchor the reveal to the current height: it is the per-hand timeout floor baseline (audit 3), so
    // the first betting turn's deadline is measured from when the hand's entropy went on the record.
    const startHeight = await this.nowHeight();
    const revealEnv: Envelope = {
      t: 'reveal',
      seat: this.mySeat,
      hand: handNo,
      r: bytesToHex(entropy),
      ...(startHeight !== undefined ? { h: startHeight } : {}),
    };
    // Collect commit+reveal, dropping any non-responder at the anchored deadline (audit 3). `active`
    // is the survivor set every honest client computes identically; a dropped seat forfeits its bond
    // on-chain and is excluded from THIS hand's deck and seating.
    const active = await this.collectHandshake(seats, handNo, commitEnv, revealEnv, startHeight);
    if (active.length < 2) {
      // A hand needs >= 2 players; one survivor (e.g. heads-up where the opponent vanished) cannot
      // continue — fail closed (funds recover via the pre-signed refund graph).
      throw new Error('handshake left fewer than 2 active players — hand cannot continue');
    }

    const entropies: Uint8Array[] = [];
    for (const s of active) {
      const commit = this.received((e) => e.t === 'commit' && e.seat === s.seat && e.hand === handNo)!;
      const reveal = this.received((e) => e.t === 'reveal' && e.seat === s.seat && e.hand === handNo)!;
      const r = hexToBytes(reveal.r!); // validated as hex at the trust boundary in subscribe()
      if (!constantTimeEqualHex(bytesToHex(sha256(r)), commit.c!)) throw new Error(`bad reveal for seat ${s.seat}`);
      entropies.push(r);
    }
    // Deck = composition of the ACTIVE seats' secret permutations only (a dropped non-revealer's
    // permutation is absent by construction). Deterministic: `active` is seat-sorted and identical
    // across clients, so the derived deck is identical (P2).
    const deck: Card[] = deckFromEntropies(entropies);

    // Recompute the button among the active seats deterministically: the first active seat at or after
    // the original button's seat number (wrapping). Identical input (agreed active set + buttonIndex)
    // → identical button on every client.
    const origButtonSeat = seats[buttonIndex % seats.length]!.seat;
    let activeButton = active.findIndex((s) => s.seat >= origButtonSeat);
    if (activeButton < 0) activeButton = 0; // button seat dropped past the last active → wrap to first

    this.module = createGameModule(this.ruleset.variant, deck, activeButton);
    this.state = this.module.init(this.ruleset, active.map((s) => ({ seat: s.seat, stack: s.stack })));
    this.emit();

    const cursor = new Map<number, number>();
    // Every state hash this client has occupied this hand (audit finding #12). A peer's action carries
    // `prev` = the state hash BEFORE its action; in convergent honest play it equals our CURRENT state,
    // but if we have since advanced past that state via a deterministic timeout-default for the same
    // seat, the peer's genuine-but-late action is STALE (bound to a state we held but no longer hold).
    // We tolerate stale (prev ∈ states we occupied) and REJECT forged/divergent (prev ∈ no state we
    // ever held) — distinguishing benign lateness from an action bound to a fabricated branch.
    const seenStates = new Set<string>();
    while (!this.state.handComplete) {
      seenStates.add(this.module.stateHash(this.state));
      const toAct = InteractiveNetworkedTableClient.toAct(this.state);
      if (toAct === null) break;
      if (toAct === this.mySeat) {
        const action = await new Promise<Action>((res) => {
          this.pendingAction = res;
          this.emit(); // your turn (legal actions in the update)
        });
        const prev = this.module.stateHash(this.state); // state hash BEFORE the action (audit 8)
        const actHeight = await this.nowHeight(); // anchors this turn's floor for the NEXT seat (audit 3)
        if (MP_DEBUG) console.error(`[icx seat${this.mySeat}] MINE ${action.kind} amt=${action.amount} h=${actHeight}`);
        this.state = this.module.apply(this.state, action);
        await this.publish({
          t: 'action',
          seat: this.mySeat,
          hand: handNo,
          kind: action.kind,
          amount: action.amount,
          prev,
          ...(actHeight !== undefined ? { h: actHeight } : {}),
          ...(action.discard ? { discard: action.discard } : {}),
        });
      } else {
        const seen = cursor.get(toAct) ?? 0;
        const outcome = await this.awaitPeerActionOrTimeout(toAct, handNo, seen);
        if (outcome === 'timeout') {
          // The seat missed the anchored deadline: apply the engine's check-or-fold default (audit 3).
          // Deterministic and replayable — every client computes the identical default from the state.
          // A timeout consumes NO real envelope, so the cursor is NOT advanced: a flaky seat's later
          // genuine actions stay index-aligned (a folded seat simply never reaches this branch again).
          const def = this.defaultAction(toAct);
          if (MP_DEBUG) console.error(`[icx seat${this.mySeat}] DROP seat=${toAct} -> ${def.kind}`);
          this.state = this.module.apply(this.state, def);
        } else {
          // Bind the peer's action to the AGREED state before applying it (audit finding #12, §8). The
          // signature already authenticated `prev` (envelopeMessage); this is the INTEGRITY check that
          // it is bound to a state we actually hold.
          const expectedPrev = this.module.stateHash(this.state);
          if (outcome.prev !== undefined && outcome.prev !== expectedPrev && seenStates.has(outcome.prev)) {
            // STALE: authentic, but bound to a state we already advanced past (e.g. a timeout-default we
            // applied for this seat superseded it). Ignore it — do NOT apply — and step the cursor past
            // it so the seat's NEXT genuine action (bound to the current state) is the one we apply.
            if (MP_DEBUG) console.error(`[icx seat${this.mySeat}] STALE peer seat=${toAct} prev=${outcome.prev.slice(0, 8)} — skip`);
            cursor.set(toAct, seen + 1);
            this.emit();
            continue;
          }
          if (outcome.prev !== expectedPrev) {
            // FORGED / DIVERGENT: bound to a state this client has NEVER occupied this hand (a fabricated
            // branch, or a replay onto the wrong state) — fail closed rather than apply it.
            throw new Error(
              `peer seat ${toAct} action prior-state hash mismatch (got ${outcome.prev ?? 'none'}, expected ${expectedPrev}) — ` +
                `state divergence, aborting hand (audit #12)`,
            );
          }
          if (MP_DEBUG) console.error(`[icx seat${this.mySeat}] APPLY peer seat=${toAct} ${outcome.kind} amt=${outcome.amount ?? 0}`);
          cursor.set(toAct, seen + 1);
          this.state = this.module.apply(this.state, {
            kind: outcome.kind!,
            seat: toAct,
            amount: outcome.amount ?? 0,
            ...(outcome.discard ? { discard: outcome.discard } : {}),
          });
        }
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
