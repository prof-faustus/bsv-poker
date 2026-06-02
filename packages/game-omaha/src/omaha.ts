/**
 * Omaha / Pot-Limit Omaha GameModule — core §7.3.1, REQ-FSM-006.
 *
 * Omaha is the Texas Hold'em FSM (§19.E) with exactly TWO overrides (REQ-FSM-006):
 *   (i)  DEAL_HOLE draws FOUR concealed cards per seat (not two);
 *   (ii) SHOWDOWN evaluates by the Omaha-constrained evaluator — exactly 2 of 4 hole + exactly
 *        3 of 5 board (§5.3.2, REQ-POKER-005). The generic best-of-7 is provably WRONG for
 *        Omaha (§19.D), so this module routes showdown through `bestOmaha`.
 * Everything else — streets preflop/flop/turn/river, the 3-1-1 board, side pots, the
 * two-exit timeout-default rule (P4) — is identical to Hold'em.
 *
 * Determinism (P2 / REQ-ARCH-002): pure function of (ruleset, injected recorded deck, actions);
 * no randomness. The deck is the post-shuffle order (core §4). Card faces are engine-known for
 * settlement but concealed at the UI boundary; cooperative reveals auto-advance and their
 * timeout-default is reported by isTimeoutEligible (core §6.4).
 *
 * The common structure is Pot-Limit (PLO); the module accepts the ruleset's structure (NL/PL/FL,
 * D3) unchanged — the betting machine already implements all three.
 *
 * Omaha Hi-Lo (Omaha-8, REQ-FSM-007) — the ace-to-five eight-or-better low split — is a
 * separate, test-vectored path that is NOT implemented here unless `ruleset.hiLo` is set; see
 * the TODO at SHOWDOWN below. The hand-eval package already exposes `bestOmaha8Low` for it.
 */

import {
  type Action,
  type BettingState,
  type Card,
  type GameState,
  type LegalActions,
  type Payouts,
  type Pot,
  type Ruleset,
  type SeatState,
  ByteWriter,
  bytesToHex,
  sha256,
} from '@bsv-poker/protocol-types';
import { bestOmaha, compareHigh, bestOmaha8Low, compareLow } from '@bsv-poker/hand-eval';
import {
  type BettingCtx,
  type BettingSeat,
  type GameModule,
  type SeatInit,
  type TimeoutResolution,
  applyAction,
  computePots,
  awardPot,
  isRoundClosed,
  legalActions,
  openRound,
} from '@bsv-poker/engine';

/** Number of concealed hole cards per seat — the first Omaha override (REQ-FSM-006). */
const HOLE_CARDS = 4;

// §19.E phases (identical graph to Hold'em).
export const PHASES = {
  TABLE_LOCKED: 'S0_TABLE_LOCKED',
  POST_BLINDS: 'S1_POST_BLINDS',
  SHUFFLE: 'S2_SHUFFLE',
  DEAL_HOLE: 'S3_DEAL_HOLE',
  BET_PREFLOP: 'S4_BET_PREFLOP',
  REVEAL_FLOP: 'S5_REVEAL_FLOP',
  BET_FLOP: 'S6_BET_FLOP',
  REVEAL_TURN: 'S7_REVEAL_TURN',
  BET_TURN: 'S8_BET_TURN',
  REVEAL_RIVER: 'S9_REVEAL_RIVER',
  BET_RIVER: 'S10_BET_RIVER',
  SHOWDOWN: 'S11_SHOWDOWN',
  SETTLE: 'S12_SETTLE',
  HAND_END: 'S13_HAND_END',
  FOLD_END: 'FOLD_END',
  RECOVERY: 'RECOVERY',
} as const;

export interface OmahaState extends GameState {
  readonly ctx: BettingCtx;
  readonly deck: readonly Card[];
  /** Engine-known hole cards per seat number (4 each); concealed at the UI boundary. */
  readonly hole: Readonly<Record<number, readonly Card[]>>;
  readonly payouts: Payouts;
}

interface OmahaConfig {
  /** The post-shuffle deck (>= 4*seats + 5 cards). Required for a real hand. */
  readonly deck: readonly Card[];
}

const BET_PHASES = new Set<string>([
  PHASES.BET_PREFLOP,
  PHASES.BET_FLOP,
  PHASES.BET_TURN,
  PHASES.BET_RIVER,
]);

function projectSeats(ctx: BettingCtx): SeatState[] {
  return ctx.seats.map((s) => ({
    seat: s.seat,
    stack: s.stack,
    committedThisRound: s.committedThisRound,
    committedThisHand: s.committedThisHand,
    folded: s.folded,
    allIn: s.allIn,
    hasActedThisRound: s.hasActedThisRound,
    holeSlots: [],
  }));
}

function projectBetting(ctx: BettingCtx): BettingState {
  return {
    betToCall: ctx.betToCall,
    lastFullRaise: ctx.lastFullRaise,
    toAct: ctx.toAct,
    lastAggressor: ctx.lastAggressor,
    raisesThisStreet: ctx.raisesThisStreet,
  };
}

function liveSeats(ctx: BettingCtx): number[] {
  return ctx.seats.filter((s) => !s.folded).map((s) => s.seat);
}

function seatOrder(ctx: BettingCtx): number[] {
  return ctx.seats.map((s) => s.seat).sort((a, b) => a - b);
}

function nonButton(ctx: BettingCtx, button: number): number {
  const order = seatOrder(ctx);
  const idx = order.indexOf(button);
  return order[(idx + 1) % order.length]!;
}

function freshState(base: OmahaState, ctx: BettingCtx, phase: string): OmahaState {
  return {
    ...base,
    ctx,
    phase,
    seats: projectSeats(ctx),
    betting: projectBetting(ctx),
  };
}

export type OmahaModule = GameModule<OmahaState> & { stateHash: (s: OmahaState) => string };

export function createOmaha(config: OmahaConfig): OmahaModule {
  const deck = config.deck;
  let rulesetRef: Ruleset | null = null;

  function init(ruleset: Ruleset, seatInits: SeatInit[]): OmahaState {
    if (ruleset.variant !== 'omaha') throw new Error('not an omaha ruleset');
    if (seatInits.length < 2) throw new Error('need >= 2 seats');
    // Override (i): FOUR concealed cards/seat then a 5-card board.
    const need = HOLE_CARDS * seatInits.length + 5;
    if (deck.length < need) throw new Error(`deck too small: need ${need}, got ${deck.length}`);

    const order = [...seatInits].sort((a, b) => a.seat - b.seat);
    const buttonSeat = order[0]!.seat; // Phase-1: button at the lowest seat
    const sb = buttonSeat;
    const bb = order[1 % order.length]!.seat;

    // Deal hole cards one at a time, button-first, from the injected deck (4 rounds).
    const hole: Record<number, Card[]> = {};
    for (const s of order) hole[s.seat] = [];
    let p = 0;
    for (let k = 0; k < HOLE_CARDS; k++) {
      for (const s of order) hole[s.seat]!.push(deck[p++]!);
    }

    const bseats: BettingSeat[] = order.map((s) => ({
      seat: s.seat,
      stack: s.stack,
      committedThisRound: 0,
      committedThisHand: 0,
      folded: false,
      allIn: false,
      hasActedThisRound: false,
      mayRaise: true,
    }));
    const ctx: BettingCtx = {
      seats: bseats,
      betToCall: 0,
      lastFullRaise: 0,
      toAct: null,
      lastAggressor: null,
      raisesThisStreet: 0,
      betLevel: 'big',
    };
    const post = (seat: number, amount: number): void => {
      const s = ctx.seats.find((x) => x.seat === seat)!;
      const amt = Math.min(amount, s.stack);
      s.stack -= amt;
      s.committedThisRound += amt;
      s.committedThisHand += amt;
      if (s.stack === 0) s.allIn = true;
    };
    post(sb, ruleset.blinds.smallBlind);
    post(bb, ruleset.blinds.bigBlind);
    ctx.betToCall = ruleset.blinds.bigBlind;
    ctx.lastFullRaise = ruleset.blinds.bigBlind;
    ctx.lastAggressor = bb;
    ctx.toAct = sb; // heads-up: button/SB acts first preflop

    const base: OmahaState = {
      rulesetHash: '',
      gid: '',
      phase: PHASES.BET_PREFLOP,
      handNumber: 0,
      buttonSeat,
      seats: projectSeats(ctx),
      board: [],
      betting: projectBetting(ctx),
      pots: [],
      handComplete: false,
      ctx,
      deck,
      hole,
      payouts: [],
    };
    rulesetRef = ruleset;
    return base;
  }

  /** Board card indices in the deck after the 4-per-seat hole cards. */
  function boardSlots(state: OmahaState): { flop: Card[]; turn: Card; river: Card } {
    const n = state.ctx.seats.length;
    const start = HOLE_CARDS * n;
    return {
      flop: [state.deck[start]!, state.deck[start + 1]!, state.deck[start + 2]!],
      turn: state.deck[start + 3]!,
      river: state.deck[start + 4]!,
    };
  }

  function autoAdvance(state: OmahaState): OmahaState {
    let s = state;
    for (;;) {
      if (liveSeats(s.ctx).length <= 1 && !s.handComplete) {
        s = freshState(s, s.ctx, PHASES.SETTLE);
        return settleState(s);
      }
      if (BET_PHASES.has(s.phase)) {
        if (!isRoundClosed(s.ctx)) return s;
        s = nextStreet(s);
        continue;
      }
      if (s.phase === PHASES.SHOWDOWN) {
        s = freshState(s, s.ctx, PHASES.SETTLE);
        return settleState(s);
      }
      return s;
    }
  }

  function nextStreet(state: OmahaState): OmahaState {
    const { flop, turn, river } = boardSlots(state);
    const firstPost = nonButton(state.ctx, state.buttonSeat);
    switch (state.phase) {
      case PHASES.BET_PREFLOP: {
        const board = [...flop];
        const ctx = openRound(state.ctx, firstPost, 'big');
        return { ...freshState(state, ctx, PHASES.BET_FLOP), board };
      }
      case PHASES.BET_FLOP: {
        const board = [...state.board, turn];
        const ctx = openRound(state.ctx, firstPost, 'big');
        return { ...freshState(state, ctx, PHASES.BET_TURN), board };
      }
      case PHASES.BET_TURN: {
        const board = [...state.board, river];
        const ctx = openRound(state.ctx, firstPost, 'big');
        return { ...freshState(state, ctx, PHASES.BET_RIVER), board };
      }
      case PHASES.BET_RIVER: {
        return freshState(state, state.ctx, PHASES.SHOWDOWN);
      }
      default:
        throw new Error(`nextStreet from non-betting phase ${state.phase}`);
    }
  }

  function settleState(state: OmahaState): OmahaState {
    const pots: Pot[] = computePots(
      state.ctx.seats.map((s) => ({ seat: s.seat, contrib: s.committedThisHand, folded: s.folded })),
    );
    const order = seatOrder(state.ctx);
    const bIdx = order.indexOf(state.buttonSeat);
    const leftOfButton = [...order.slice(bIdx + 1), ...order.slice(0, bIdx + 1)];

    // Override (ii): Omaha-constrained showdown — exactly 2 hole + 3 board (REQ-POKER-005).
    const folded = (s: number): boolean => state.ctx.seats.find((x) => x.seat === s)!.folded;
    const handValue = (seat: number) => bestOmaha(state.hole[seat]!, state.board).value;
    const cmpHigh = (a: number, b: number): -1 | 0 | 1 => {
      if (folded(a) && folded(b)) return 0;
      if (folded(a)) return -1;
      if (folded(b)) return 1;
      return compareHigh(handValue(a), handValue(b));
    };

    // Omaha Hi-Lo (Omaha-8, REQ-FSM-007): split each pot — high half to the best high; low half
    // to the best qualifying eight-or-better low (bestOmaha8Low); no qualifying low ⇒ high scoops.
    const lowOf = (seat: number) =>
      folded(seat) ? null : bestOmaha8Low(state.hole[seat]!, state.board);
    const cmpLow = (a: number, b: number): -1 | 0 | 1 => {
      const la = lowOf(a);
      const lb = lowOf(b);
      if (!la && !lb) return 0;
      if (!la) return -1; // a has no qualifying low → loses the low
      if (!lb) return 1;
      const c = compareLow(la.value, lb.value); // lower is better
      return c === 0 ? 0 : c < 0 ? 1 : -1;
    };

    const awards = new Map<number, number>();
    const add = (seat: number, amt: number): void => {
      awards.set(seat, (awards.get(seat) ?? 0) + amt);
    };
    for (const pot of pots) {
      if (pot.eligible.length === 1) {
        add(pot.eligible[0]!, pot.amount);
        continue;
      }
      if (!rulesetRef!.hiLo) {
        for (const [s, amt] of awardPot(pot, cmpHigh, leftOfButton)) add(s, amt);
        continue;
      }
      // hi-lo: split. Odd chip goes to the high half (convention).
      const anyLow = pot.eligible.some((s) => lowOf(s) !== null);
      if (!anyLow) {
        for (const [s, amt] of awardPot(pot, cmpHigh, leftOfButton)) add(s, amt); // high scoops
        continue;
      }
      const lowHalf = Math.floor(pot.amount / 2);
      const highHalf = pot.amount - lowHalf;
      for (const [s, amt] of awardPot({ amount: highHalf, eligible: pot.eligible }, cmpHigh, leftOfButton)) add(s, amt);
      for (const [s, amt] of awardPot({ amount: lowHalf, eligible: pot.eligible }, cmpLow, leftOfButton)) add(s, amt);
    }

    const ctx: BettingCtx = {
      ...state.ctx,
      seats: state.ctx.seats.map((s) => ({ ...s, stack: s.stack + (awards.get(s.seat) ?? 0) })),
    };
    const payouts: Payouts = [...awards.entries()].map(([seat, amount]) => ({ seat, amount }));
    return { ...freshState(state, ctx, PHASES.HAND_END), pots, payouts, handComplete: true };
  }

  function getLegalActions(state: OmahaState, seat: number): LegalActions {
    if (!BET_PHASES.has(state.phase)) return { check: false, fold: false };
    if (state.ctx.toAct !== seat) return { check: false, fold: false };
    return legalActions(state.ctx, rulesetRef!, seat);
  }

  function apply(state: OmahaState, action: Action): OmahaState {
    if (!BET_PHASES.has(state.phase)) {
      throw new Error(`no player action accepted in phase ${state.phase}`);
    }
    if (state.ctx.toAct !== action.seat) throw new Error(`not seat ${action.seat}'s turn`);
    const ctx = applyAction(state.ctx, rulesetRef!, action);
    const advanced = freshState(state, ctx, state.phase);
    return autoAdvance(advanced);
  }

  function isTimeoutEligible(state: OmahaState, now: number): TimeoutResolution | null {
    void now;
    if (!BET_PHASES.has(state.phase) || state.ctx.toAct === null) return null;
    const seat = state.ctx.toAct;
    const legal = legalActions(state.ctx, rulesetRef!, seat);
    const defaultAction: Action = legal.check
      ? { kind: 'check', seat, amount: 0 }
      : { kind: 'fold', seat, amount: 0 };
    return { seat, defaultAction };
  }

  function isHandComplete(state: OmahaState): boolean {
    return state.handComplete;
  }

  function settle(state: OmahaState): Payouts {
    return state.payouts;
  }

  function serialize(state: OmahaState): Uint8Array {
    const w = new ByteWriter();
    w.str(state.phase);
    w.u32(state.handNumber);
    w.u8(state.buttonSeat);
    w.arr(state.board, (ww, c) => ww.u8(c));
    w.arr(state.seats, (ww, s) => {
      ww.u8(s.seat).u64(s.stack).u64(s.committedThisRound).u64(s.committedThisHand);
      ww.bool(s.folded).bool(s.allIn);
    });
    w.u64(state.betting.betToCall).u64(state.betting.lastFullRaise);
    w.bool(state.handComplete);
    return w.toBytes();
  }

  function stateHash(state: OmahaState): string {
    return bytesToHex(sha256(serialize(state)));
  }

  const module: OmahaModule = {
    id: 'omaha',
    init,
    getLegalActions,
    apply,
    isTimeoutEligible,
    isHandComplete,
    settle,
    serialize,
    stateHash,
  };
  return module;
}
