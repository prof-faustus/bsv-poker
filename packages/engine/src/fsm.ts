/**
 * Game-module FSM framework — core §7.1, REQ-FSM-001. A game module is a pure function of
 * its inputs: no I/O, no networking, no time reads, no randomness (P2 / REQ-ARCH-002).
 * Every actionable state enumerates its successors including the timeout-default (P4).
 *
 * Note on the contract: core §7.1/§15.2 type getLegalActions as `Action[]`; this engine
 * returns the richer `LegalActions` descriptor (the canonical superset) and exposes
 * `enumerateActions` to produce the literal `Action[]` — a refinement, not a contradiction.
 */

import type {
  Action,
  GameState,
  LegalActions,
  Payouts,
  Ruleset,
  Variant,
} from '@bsv-poker/protocol-types';

/** What a decision/recovery timeout resolves to (core §6.4): the safe default move. */
export interface TimeoutResolution {
  readonly seat: number;
  readonly defaultAction: Action;
}

export interface GameModule<S extends GameState = GameState> {
  readonly id: Variant;
  init(ruleset: Ruleset, seats: SeatInit[]): S;
  getLegalActions(state: S, seat: number): LegalActions;
  apply(state: S, action: Action): S;
  /** Timeout-eligibility for the seat on the clock; null if none is eligible at `now`. */
  isTimeoutEligible(state: S, now: number): TimeoutResolution | null;
  isHandComplete(state: S): boolean;
  settle(state: S): Payouts;
  serialize(state: S): Uint8Array;
}

export interface SeatInit {
  readonly seat: number;
  readonly stack: number;
}

/** Enumerate concrete legal actions for a seat (the literal core §7.1 `Action[]` contract). */
export function enumerateActions(legal: LegalActions, seat: number): Action[] {
  const out: Action[] = [];
  if (legal.fold) out.push({ kind: 'fold', seat, amount: 0 });
  if (legal.check) out.push({ kind: 'check', seat, amount: 0 });
  if (legal.call) out.push({ kind: 'call', seat, amount: legal.call.amount });
  if (legal.bet) out.push({ kind: 'bet', seat, amount: legal.bet.min });
  if (legal.raise) out.push({ kind: 'raise', seat, amount: legal.raise.min });
  return out;
}

/** Replay an ordered action list from a fresh hand — the deterministic-core driver (P2). */
export function replay<S extends GameState>(
  module: GameModule<S>,
  ruleset: Ruleset,
  seats: SeatInit[],
  actions: readonly Action[],
): S {
  let state = module.init(ruleset, seats);
  for (const a of actions) state = module.apply(state, a);
  return state;
}
