/**
 * Ruleset SDK surface (core §15.3): validate, hash, resolveDefaultAction.
 */

import {
  type Action,
  type Ruleset,
  rulesetHash,
  BETTING_STRUCTURES,
  VARIANTS,
} from '@bsv-poker/protocol-types';

export interface RulesetError {
  readonly field: string;
  readonly message: string;
}

/** Validate a ruleset; returns the list of problems (empty = valid). Fail-closed (§A10.4). */
export function validateRuleset(r: Ruleset): RulesetError[] {
  const errs: RulesetError[] = [];
  if (!VARIANTS.includes(r.variant)) errs.push({ field: 'variant', message: 'unknown variant' });
  if (!BETTING_STRUCTURES.includes(r.bettingStructure))
    errs.push({ field: 'bettingStructure', message: 'unknown structure' });
  if (r.seats < 2 || r.seats > 9) errs.push({ field: 'seats', message: 'seats must be 2..9 (D2)' });
  if (r.bettingStructure === 'FL' && !r.flSizing)
    errs.push({ field: 'flSizing', message: 'Fixed-Limit requires flSizing' });
  if (r.forcedBetModel === 'blinds' && r.blinds.bigBlind < r.blinds.smallBlind)
    errs.push({ field: 'blinds', message: 'bigBlind must be >= smallBlind' });
  if (r.minBuyIn <= 0 || r.maxBuyIn < r.minBuyIn)
    errs.push({ field: 'buyIn', message: 'require 0 < minBuyIn <= maxBuyIn' });
  if (r.timeouts.recoveryMs <= r.timeouts.decisionMs)
    errs.push({ field: 'timeouts', message: 'recoveryMs must exceed decisionMs' });
  return errs;
}

/** rulesetHash = H(canonicalSerialize(Ruleset)) — core §5.2, REQ-POKER-002. */
export function hashRuleset(r: Ruleset): string {
  return rulesetHash(r);
}

/**
 * The safe default-on-timeout for a facing-a-bet decision (core §6.4): check if checking is
 * legal, else fold — NEVER a forced wager. (The engine's isTimeoutEligible computes the actual
 * eligible seat + default; this is the policy helper.)
 */
export function resolveDefaultAction(seat: number, facingBet: boolean): Action {
  return facingBet ? { kind: 'fold', seat, amount: 0 } : { kind: 'check', seat, amount: 0 };
}
