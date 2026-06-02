/**
 * Texas Hold'em GameModule — core §7.2 and the §19.E transition table. Phase-1 reference is
 * heads-up No-Limit on regtest (D1), but the module generalises to the 2–9 seat envelope.
 *
 * Determinism (P2 / REQ-ARCH-002): the module is a pure function of (ruleset, injected deck,
 * betting actions). The deck is the post-shuffle order — a RECORDED source (core §4); the
 * module never samples randomness. Card faces are engine-known for settlement but are
 * "concealed" at the UI/custody boundary (core §11.5); selective reveal and the N-of-N board
 * reveals are added by the crypto/tx layer (core §4.6, §6.6). Here the cooperative reveal
 * path auto-advances; its timeout-default is reported by isTimeoutEligible (core §6.4).
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
import { bestHigh, compareHigh } from '@bsv-poker/hand-eval';
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

// §19.E phases.
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

export interface HoldemState extends GameState {
  /** Internal betting context (source of truth; GameState.seats/betting project from it). */
  readonly ctx: BettingCtx;
  /** Injected post-shuffle deck (recorded source). */
  readonly deck: readonly Card[];
  /** Engine-known hole cards per seat number; concealed at the UI boundary. */
  readonly hole: Readonly<Record<number, readonly Card[]>>;
  /** Awards computed at SETTLE (per seat gross winnings). */
  readonly payouts: Payouts;
}

interface HoldemConfig {
  /** The post-shuffle deck (>= 2*seats + 5 cards). Required for a real hand. */
  readonly deck: readonly Card[];
  /** Button seat index into the ascending seat order (rotation across hands, §19.E S13). */
  readonly buttonIndex?: number;
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
    holeSlots: [], // slot bookkeeping lives in the crypto/tx layer
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

/** Heads-up: button = SB acts first preflop; non-button acts first postflop. */
function seatOrder(ctx: BettingCtx): number[] {
  return ctx.seats.map((s) => s.seat).sort((a, b) => a - b);
}

function nonButton(ctx: BettingCtx, button: number): number {
  // For heads-up / first-active-left-of-button postflop.
  const order = seatOrder(ctx);
  const idx = order.indexOf(button);
  return order[(idx + 1) % order.length]!;
}

/** The first non-folded, non-all-in seat at or clockwise after `startSeat`. */
function firstActiveFrom(ctx: BettingCtx, startSeat: number): number {
  const order = seatOrder(ctx);
  const start = order.indexOf(startSeat);
  for (let i = 0; i < order.length; i++) {
    const seat = order[(start + i) % order.length]!;
    const s = ctx.seats.find((x) => x.seat === seat)!;
    if (!s.folded && !s.allIn) return seat;
  }
  return startSeat;
}

function freshState(base: HoldemState, ctx: BettingCtx, phase: string): HoldemState {
  return {
    ...base,
    ctx,
    phase,
    seats: projectSeats(ctx),
    betting: projectBetting(ctx),
  };
}

export type HoldemModule = GameModule<HoldemState> & { stateHash: (s: HoldemState) => string };

export function createHoldem(config: HoldemConfig): HoldemModule {
  const deck = config.deck;

  function init(ruleset: Ruleset, seatInits: SeatInit[]): HoldemState {
    if (ruleset.variant !== 'holdem') throw new Error('not a holdem ruleset');
    if (seatInits.length < 2) throw new Error('need >= 2 seats');
    const need = 2 * seatInits.length + 5;
    if (deck.length < need) throw new Error(`deck too small: need ${need}, got ${deck.length}`);

    const order = [...seatInits].sort((a, b) => a.seat - b.seat);
    const seatNums = order.map((s) => s.seat);
    const N = order.length;
    const bIdx = config.buttonIndex ?? 0; // button seat index (rotation across hands, §19.E S13)
    const buttonSeat = seatNums[bIdx % N]!;
    // Heads-up: button = SB and acts first preflop. 3+: SB = left of button, UTG acts first.
    const sbIdx = N === 2 ? bIdx : bIdx + 1;
    const bbIdx = N === 2 ? bIdx + 1 : bIdx + 2;
    const preflopFirstIdx = N === 2 ? bIdx : bIdx + 3;
    const sb = seatNums[sbIdx % N]!;
    const bb = seatNums[bbIdx % N]!;

    // Deal hole cards (one at a time, button-first) from the injected deck.
    const hole: Record<number, Card[]> = {};
    for (const s of order) hole[s.seat] = [];
    let p = 0;
    for (let k = 0; k < 2; k++) {
      for (const s of order) hole[s.seat]!.push(deck[p++]!);
    }

    // Build betting seats and post blinds.
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
    ctx.toAct = firstActiveFrom(ctx, seatNums[preflopFirstIdx % N]!);

    const rulesetHashHex = ''; // filled by the SDK when a real ruleset hash is bound
    const base: HoldemState = {
      rulesetHash: rulesetHashHex,
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
    // store ruleset on the closure for apply()
    rulesetRef = ruleset;
    return base;
  }

  // Captured ruleset (the module instance plays one ruleset; matches a table).
  let rulesetRef: Ruleset | null = null;

  /** Board card indices in the deck after the hole cards. */
  function boardSlots(state: HoldemState): { flop: Card[]; turn: Card; river: Card } {
    const n = state.ctx.seats.length;
    const start = 2 * n;
    return {
      flop: [state.deck[start]!, state.deck[start + 1]!, state.deck[start + 2]!],
      turn: state.deck[start + 3]!,
      river: state.deck[start + 4]!,
    };
  }

  /** Advance through reveal/showdown/settle until the next state that awaits a player action. */
  function autoAdvance(state: HoldemState): HoldemState {
    let s = state;
    for (;;) {
      // Fold-end: only one live player → award uncontested (no reveal).
      if (liveSeats(s.ctx).length <= 1 && !s.handComplete) {
        s = freshState(s, s.ctx, PHASES.SETTLE);
        return settleState(s);
      }
      if (BET_PHASES.has(s.phase)) {
        if (!isRoundClosed(s.ctx)) return s; // awaits action
        s = nextStreet(s); // round closed → reveal + open next betting round (or showdown)
        continue;
      }
      if (s.phase === PHASES.SHOWDOWN) {
        s = freshState(s, s.ctx, PHASES.SETTLE);
        return settleState(s);
      }
      return s;
    }
  }

  function nextStreet(state: HoldemState): HoldemState {
    const { flop, turn, river } = boardSlots(state);
    const firstPost = firstActiveFrom(state.ctx, nonButton(state.ctx, state.buttonSeat));
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

  function settleState(state: HoldemState): HoldemState {
    const live = liveSeats(state.ctx);
    const pots: Pot[] = computePots(
      state.ctx.seats.map((s) => ({ seat: s.seat, contrib: s.committedThisHand, folded: s.folded })),
    );
    // seat order left-of-button for odd-chip rule.
    const order = seatOrder(state.ctx);
    const bIdx = order.indexOf(state.buttonSeat);
    const leftOfButton = [...order.slice(bIdx + 1), ...order.slice(0, bIdx + 1)];

    // Comparator over seats by best 5-card high hand (hole + board). Folded seats lose.
    const handValue = (seat: number) => bestHigh([...state.hole[seat]!, ...state.board]).value;
    const cmp = (a: number, b: number): -1 | 0 | 1 => {
      const fa = state.ctx.seats.find((x) => x.seat === a)!.folded;
      const fb = state.ctx.seats.find((x) => x.seat === b)!.folded;
      if (fa && fb) return 0;
      if (fa) return -1;
      if (fb) return 1;
      return compareHigh(handValue(a), handValue(b));
    };

    const awards = new Map<number, number>();
    for (const pot of pots) {
      // Uncontested fold-end: a board may be empty; if only one live, award directly.
      if (pot.eligible.length === 1) {
        awards.set(pot.eligible[0]!, (awards.get(pot.eligible[0]!) ?? 0) + pot.amount);
        continue;
      }
      const a = awardPot(pot, cmp, leftOfButton);
      for (const [seat, amt] of a) awards.set(seat, (awards.get(seat) ?? 0) + amt);
    }

    // Apply awards to stacks.
    const ctx: BettingCtx = {
      ...state.ctx,
      seats: state.ctx.seats.map((s) => ({
        ...s,
        stack: s.stack + (awards.get(s.seat) ?? 0),
      })),
    };
    const payouts: Payouts = [...awards.entries()].map(([seat, amount]) => ({ seat, amount }));
    void live;
    return {
      ...freshState(state, ctx, PHASES.HAND_END),
      pots,
      payouts,
      handComplete: true,
    };
  }

  function getLegalActions(state: HoldemState, seat: number): LegalActions {
    if (!BET_PHASES.has(state.phase)) return { check: false, fold: false };
    if (state.ctx.toAct !== seat) return { check: false, fold: false };
    return legalActions(state.ctx, rulesetRef!, seat);
  }

  function apply(state: HoldemState, action: Action): HoldemState {
    if (!BET_PHASES.has(state.phase)) {
      throw new Error(`no player action accepted in phase ${state.phase}`);
    }
    if (state.ctx.toAct !== action.seat) throw new Error(`not seat ${action.seat}'s turn`);
    const ctx = applyAction(state.ctx, rulesetRef!, action);
    const advanced = freshState(state, ctx, state.phase);
    return autoAdvance(advanced);
  }

  function isTimeoutEligible(state: HoldemState, now: number): TimeoutResolution | null {
    void now; // "now" is anchored height/time (core §6.4); the caller applies only after maturity.
    if (!BET_PHASES.has(state.phase) || state.ctx.toAct === null) return null;
    const seat = state.ctx.toAct;
    const legal = legalActions(state.ctx, rulesetRef!, seat);
    // Safe default: check if legal, else fold — NEVER a forced wager (core §6.4).
    const defaultAction: Action = legal.check
      ? { kind: 'check', seat, amount: 0 }
      : { kind: 'fold', seat, amount: 0 };
    return { seat, defaultAction };
  }

  function isHandComplete(state: HoldemState): boolean {
    return state.handComplete;
  }

  function settle(state: HoldemState): Payouts {
    return state.payouts;
  }

  function serialize(state: HoldemState): Uint8Array {
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

  /** Stable hash of the public state (for branch binding / replay equivalence, core §6.3). */
  function stateHash(state: HoldemState): string {
    return bytesToHex(sha256(serialize(state)));
  }

  const module: HoldemModule = {
    id: 'holdem',
    init,
    getLegalActions,
    apply,
    isTimeoutEligible,
    isHandComplete,
    settle,
    serialize,
    stateHash, // exposed for branch binding / replay equivalence
  };
  return module;
}
