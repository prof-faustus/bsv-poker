/**
 * Betting structures and the betting machine — core §5.4, REQ-POKER-008/009/010.
 *
 * Strategy behind one interface: No-Limit (max = stack), Pot-Limit (max = pot + call),
 * Fixed-Limit (fixed small/big bet, capped raises). The machine tracks per-seat stack,
 * committed-this-round, committed-this-hand, bet-to-call, last full raise, who is all-in,
 * who has acted since the last aggressive action, and the round-closed condition. A short
 * all-in that is not a full raise does NOT reopen betting to players already acted
 * (REQ-POKER-010).
 */

import type { Action, BettingStructure, LegalActions, Ruleset } from '@bsv-poker/protocol-types';

export interface BettingSeat {
  seat: number;
  stack: number;
  committedThisRound: number;
  committedThisHand: number;
  folded: boolean;
  allIn: boolean;
  hasActedThisRound: boolean;
  /**
   * Whether this seat currently holds the option to raise. Cleared for seats that have
   * already acted when a SHORT all-in (less than a full raise) bumps the bet — they may only
   * call, never re-raise (REQ-POKER-010). Restored on any full bet/raise.
   */
  mayRaise: boolean;
}

export interface BettingCtx {
  seats: BettingSeat[];
  betToCall: number;
  lastFullRaise: number;
  toAct: number | null;
  lastAggressor: number | null;
  raisesThisStreet: number;
  /** For Fixed-Limit: the bet level applicable to the current street. */
  betLevel: 'small' | 'big';
}

function clone(ctx: BettingCtx): BettingCtx {
  return {
    ...ctx,
    seats: ctx.seats.map((s) => ({ ...s })),
  };
}

function find(ctx: BettingCtx, seat: number): BettingSeat {
  const s = ctx.seats.find((x) => x.seat === seat);
  if (!s) throw new Error(`no such seat ${seat}`);
  return s;
}

function liveNonAllIn(ctx: BettingCtx): BettingSeat[] {
  return ctx.seats.filter((s) => !s.folded && !s.allIn);
}

function liveSeats(ctx: BettingCtx): BettingSeat[] {
  return ctx.seats.filter((s) => !s.folded);
}

/** The base bet size for the structure on this street (the minimum opening bet / raise step). */
function betUnit(ctx: BettingCtx, ruleset: Ruleset): number {
  if (ruleset.bettingStructure === 'FL') {
    if (!ruleset.flSizing) throw new Error('FL ruleset missing flSizing');
    return ctx.betLevel === 'big' ? ruleset.flSizing.bigBet : ruleset.flSizing.smallBet;
  }
  return ruleset.blinds.bigBlind;
}

function potSize(ctx: BettingCtx): number {
  return ctx.seats.reduce((s, x) => s + x.committedThisHand, 0);
}

/** Maximum bet/raise-TO for the structure, given the acting seat. */
function maxFor(
  structure: BettingStructure,
  ctx: BettingCtx,
  seat: BettingSeat,
  isRaise: boolean,
): number {
  const allInTo = seat.committedThisRound + seat.stack; // total this round if all-in
  if (structure === 'NL') return allInTo;
  if (structure === 'PL') {
    const toCall = Math.max(0, ctx.betToCall - seat.committedThisRound);
    const potAfterCall = potSize(ctx) + toCall;
    const cap = isRaise ? ctx.betToCall + potAfterCall : potAfterCall;
    return Math.min(cap, allInTo);
  }
  // FL: max == min (fixed). Caller computes the fixed target; clamp to all-in.
  return allInTo;
}

export function legalActions(ctx: BettingCtx, ruleset: Ruleset, seat: number): LegalActions {
  const s = find(ctx, seat);
  if (s.folded || s.allIn) {
    return { check: false, fold: false };
  }
  const toCall = Math.max(0, ctx.betToCall - s.committedThisRound);
  const unit = betUnit(ctx, ruleset);
  const structure = ruleset.bettingStructure;
  const flCapped =
    structure === 'FL' && ruleset.flSizing
      ? ctx.raisesThisStreet >= ruleset.flSizing.maxRaisesPerStreet
      : false;

  const out: {
    check: boolean;
    call?: { amount: number };
    bet?: { min: number; max: number };
    raise?: { min: number; max: number };
    fold: boolean;
    draw?: boolean;
  } = { check: toCall === 0, fold: true };

  if (toCall > 0) {
    out.call = { amount: Math.min(toCall, s.stack) };
  }

  if (toCall === 0 && s.stack > 0) {
    // open bet
    const min = Math.min(unit, s.stack);
    const max = structure === 'FL' ? min : maxFor(structure, ctx, s, false);
    out.bet = { min, max };
  }

  if (toCall > 0 && s.stack > toCall && !flCapped && s.mayRaise) {
    // raise-TO
    const minRaiseTo = ctx.betToCall + Math.max(ctx.lastFullRaise, unit);
    const allInTo = s.committedThisRound + s.stack;
    if (structure === 'FL') {
      const target = ctx.betToCall + unit;
      if (target <= allInTo) out.raise = { min: target, max: target };
    } else {
      const max = maxFor(structure, ctx, s, true);
      const min = Math.min(minRaiseTo, allInTo);
      if (min <= max) out.raise = { min, max };
    }
  }

  return out;
}

/** Apply a validated action, returning a new context with toAct advanced. */
export function applyAction(ctx: BettingCtx, ruleset: Ruleset, action: Action): BettingCtx {
  const next = clone(ctx);
  const s = find(next, action.seat);
  if (s.folded || s.allIn) throw new Error(`seat ${action.seat} cannot act`);

  // Fail-closed: the move must be among the legal actions for this state (REQ-POKER-008).
  assertLegal(ctx, ruleset, action);

  const move = (target: number): void => {
    const delta = target - s.committedThisRound;
    if (delta < 0) throw new Error('negative commit');
    if (delta > s.stack) throw new Error('insufficient stack');
    s.stack -= delta;
    s.committedThisRound += delta;
    s.committedThisHand += delta;
    if (s.stack === 0) s.allIn = true;
  };

  switch (action.kind) {
    case 'check': {
      if (next.betToCall !== s.committedThisRound) throw new Error('cannot check facing a bet');
      s.hasActedThisRound = true;
      break;
    }
    case 'fold': {
      s.folded = true;
      s.hasActedThisRound = true;
      break;
    }
    case 'call': {
      // action.amount is the INCREMENTAL chips to call (== legalActions().call.amount).
      move(s.committedThisRound + action.amount);
      s.hasActedThisRound = true;
      break;
    }
    case 'bet': {
      if (next.betToCall !== 0) throw new Error('cannot bet facing a bet (use raise)');
      move(action.amount);
      next.betToCall = action.amount;
      next.lastFullRaise = action.amount;
      next.lastAggressor = s.seat;
      fullReopen(next, s.seat);
      s.hasActedThisRound = true;
      break;
    }
    case 'raise': {
      if (next.betToCall === 0) throw new Error('cannot raise without a bet (use bet)');
      if (!s.mayRaise) throw new Error('seat may not re-raise after a short all-in');
      const raiseBy = action.amount - next.betToCall;
      if (raiseBy <= 0) throw new Error('raise must exceed current bet');
      move(action.amount);
      const full = raiseBy >= next.lastFullRaise;
      next.betToCall = action.amount;
      next.lastAggressor = s.seat;
      next.raisesThisStreet += 1;
      if (full) {
        next.lastFullRaise = raiseBy;
        fullReopen(next, s.seat); // full raise reopens the option to raise for everyone
      } else {
        partialReopen(next, s.seat); // short all-in: others must respond but may only call
      }
      s.hasActedThisRound = true;
      break;
    }
    default:
      throw new Error(`betting action not handled: ${action.kind}`);
  }

  advance(next);
  return next;
}

/** Reject an action that is not legal for the current state (fail-closed). */
function assertLegal(ctx: BettingCtx, ruleset: Ruleset, action: Action): void {
  const legal = legalActions(ctx, ruleset, action.seat);
  switch (action.kind) {
    case 'check':
      if (!legal.check) throw new Error('illegal check');
      return;
    case 'fold':
      if (!legal.fold) throw new Error('illegal fold');
      return;
    case 'call':
      if (!legal.call || action.amount !== legal.call.amount) throw new Error('illegal call');
      return;
    case 'bet':
      if (!legal.bet || action.amount < legal.bet.min || action.amount > legal.bet.max)
        throw new Error('illegal bet size');
      return;
    case 'raise':
      if (!legal.raise || action.amount < legal.raise.min || action.amount > legal.raise.max)
        throw new Error('illegal raise size');
      return;
    default:
      throw new Error(`not a betting action: ${action.kind}`);
  }
}

/** Full bet/raise: every other live, non-all-in seat must act again AND regains the raise option. */
function fullReopen(ctx: BettingCtx, aggressor: number): void {
  for (const s of ctx.seats) {
    if (s.seat !== aggressor && !s.folded && !s.allIn) {
      s.hasActedThisRound = false;
      s.mayRaise = true;
    }
  }
}

/**
 * Short all-in (under a full raise): seats that have ALREADY acted must respond to the new
 * bet-to-call but may only call/fold (mayRaise cleared); seats yet to act keep their option
 * (REQ-POKER-010).
 */
function partialReopen(ctx: BettingCtx, aggressor: number): void {
  for (const s of ctx.seats) {
    if (s.seat === aggressor || s.folded || s.allIn) continue;
    if (s.hasActedThisRound) {
      s.hasActedThisRound = false;
      s.mayRaise = false;
    }
  }
}

/** Advance toAct to the next eligible seat, or null if the round is closed. */
function advance(ctx: BettingCtx): void {
  if (isRoundClosed(ctx)) {
    ctx.toAct = null;
    return;
  }
  const order = ctx.seats.map((s) => s.seat);
  const from = ctx.toAct ?? order[0]!;
  const startIdx = order.indexOf(from);
  for (let i = 1; i <= order.length; i++) {
    const seat = order[(startIdx + i) % order.length]!;
    const s = find(ctx, seat);
    if (!s.folded && !s.allIn && !s.hasActedThisRound) {
      ctx.toAct = seat;
      return;
    }
  }
  ctx.toAct = null;
}

/**
 * Round closes when action has returned to the last aggressor with all live non-all-in
 * players having matched the current bet (or checked through), OR only one live player
 * remains (REQ-POKER-010).
 */
export function isRoundClosed(ctx: BettingCtx): boolean {
  if (liveSeats(ctx).length <= 1) return true;
  const contenders = liveNonAllIn(ctx);
  if (contenders.length === 0) return true; // all remaining are all-in
  if (contenders.length === 1) {
    // Only one player can voluntarily act; there is no one to bet against. The round closes
    // once that player owes nothing (has matched the current bet). Avoids waiting for a bet
    // against all-in opponents (standard "all but one all-in" rule).
    return contenders[0]!.committedThisRound === ctx.betToCall;
  }
  return contenders.every((s) => s.hasActedThisRound && s.committedThisRound === ctx.betToCall);
}

/** Set up a fresh betting round: clear committed-this-round and acted flags. */
export function openRound(ctx: BettingCtx, firstToAct: number, betLevel: 'small' | 'big'): BettingCtx {
  const next = clone(ctx);
  for (const s of next.seats) {
    s.committedThisRound = 0;
    s.hasActedThisRound = false;
    s.mayRaise = true;
  }
  next.betToCall = 0;
  next.lastFullRaise = 0;
  next.lastAggressor = null;
  next.raisesThisStreet = 0;
  next.betLevel = betLevel;
  next.toAct = firstToAct;
  return next;
}
