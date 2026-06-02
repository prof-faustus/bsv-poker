import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  type BettingCtx,
  type BettingSeat,
  legalActions,
  applyAction,
  isRoundClosed,
  openRound,
} from '../src/betting.ts';
import type { Ruleset } from '@bsv-poker/protocol-types';

const NL: Ruleset = {
  variant: 'holdem',
  bettingStructure: 'NL',
  forcedBetModel: 'blinds',
  seats: 3,
  blinds: { smallBlind: 1, bigBlind: 2, ante: 0, bringIn: 0 },
  minBuyIn: 100,
  maxBuyIn: 200,
  timeouts: { decisionMs: 30000, recoveryMs: 120000 },
  signingMode: 'A',
  currency: 'play-regtest',
  suitTiebreakHouseRule: false,
  hiLo: false,
};

function seat(s: number, stack: number): BettingSeat {
  return {
    seat: s,
    stack,
    committedThisRound: 0,
    committedThisHand: 0,
    folded: false,
    allIn: false,
    hasActedThisRound: false,
    mayRaise: true,
  };
}

function ctx(seats: BettingSeat[], toAct: number): BettingCtx {
  return {
    seats,
    betToCall: 0,
    lastFullRaise: 0,
    toAct,
    lastAggressor: null,
    raisesThisStreet: 0,
    betLevel: 'small',
  };
}

test('NL: bet → call closes a heads-up round', () => {
  let c = ctx([seat(0, 100), seat(1, 100)], 0);
  c = applyAction(c, NL, { kind: 'bet', seat: 0, amount: 10 });
  assert.equal(c.betToCall, 10);
  assert.equal(c.toAct, 1);
  assert.equal(isRoundClosed(c), false);
  const legal = legalActions(c, NL, 1);
  assert.deepEqual(legal.call, { amount: 10 });
  assert.deepEqual(legal.raise, { min: 20, max: 100 });
  assert.equal(legal.check, false);
  c = applyAction(c, NL, { kind: 'call', seat: 1, amount: 10 });
  assert.equal(isRoundClosed(c), true);
  assert.equal(c.toAct, null);
  assert.equal(c.seats[0]!.stack, 90);
  assert.equal(c.seats[1]!.stack, 90);
});

test('NL: re-raise reopens action to the original bettor', () => {
  let c = ctx([seat(0, 100), seat(1, 100)], 0);
  c = applyAction(c, NL, { kind: 'bet', seat: 0, amount: 10 });
  c = applyAction(c, NL, { kind: 'raise', seat: 1, amount: 30 }); // raiseBy 20 ≥ 10 full
  assert.equal(c.lastFullRaise, 20);
  assert.equal(c.betToCall, 30);
  assert.equal(c.toAct, 0); // reopened to seat 0
  assert.equal(isRoundClosed(c), false);
  c = applyAction(c, NL, { kind: 'call', seat: 0, amount: 20 }); // incremental (30-10 already in)
  assert.equal(isRoundClosed(c), true);
});

test('NL: check-through closes round (no bet)', () => {
  let c = openRound(ctx([seat(0, 100), seat(1, 100)], 0), 0, 'small');
  c = applyAction(c, NL, { kind: 'check', seat: 0, amount: 0 });
  assert.equal(isRoundClosed(c), false);
  c = applyAction(c, NL, { kind: 'check', seat: 1, amount: 0 });
  assert.equal(isRoundClosed(c), true);
});

test('REQ-POKER-010: short all-in does NOT reopen the raise to a player who already acted', () => {
  // seat0 stack 100, seat1 stack 100, seat2 stack 35 (will be short all-in).
  let c = ctx([seat(0, 100), seat(1, 100), seat(2, 35)], 0);
  c = applyAction(c, NL, { kind: 'bet', seat: 0, amount: 10 }); // toAct 1
  c = applyAction(c, NL, { kind: 'raise', seat: 1, amount: 30 }); // full raise (by 20); toAct 2
  assert.equal(c.toAct, 2);
  // seat2 all-in to 35: raiseBy = 5 < lastFullRaise 20 → SHORT all-in.
  c = applyAction(c, NL, { kind: 'raise', seat: 2, amount: 35 });
  assert.equal(c.seats[2]!.allIn, true);
  assert.equal(c.betToCall, 35);
  // seat0 had NOT yet acted on seat1's full raise → it RETAINS the option to raise.
  const l0 = legalActions(c, NL, 0);
  assert.ok(l0.raise, 'seat0 still holds the raise option (had not acted on the full raise)');
  c = applyAction(c, NL, { kind: 'call', seat: 0, amount: 25 }); // 35 - 10 already in
  // seat1 made the last full raise then was short-all-in'd over → may only CALL, not re-raise.
  const l1 = legalActions(c, NL, 1);
  assert.ok(l1.call, 'seat1 must call the extra');
  assert.equal(l1.raise, undefined, 'seat1 may not re-raise a short all-in (REQ-POKER-010)');
  c = applyAction(c, NL, { kind: 'call', seat: 1, amount: 5 }); // 35 - 30 already in
  assert.equal(isRoundClosed(c), true);
});

test('NL: min-raise legality uses last full raise', () => {
  let c = ctx([seat(0, 100), seat(1, 100)], 0);
  c = applyAction(c, NL, { kind: 'bet', seat: 0, amount: 10 });
  const l = legalActions(c, NL, 1);
  // min raise-to = betToCall(10) + max(lastFullRaise(10), bb(2)) = 20
  assert.equal(l.raise!.min, 20);
});

test('fold leaves one live player → round closed', () => {
  let c = ctx([seat(0, 100), seat(1, 100)], 0);
  c = applyAction(c, NL, { kind: 'bet', seat: 0, amount: 10 });
  c = applyAction(c, NL, { kind: 'fold', seat: 1, amount: 0 });
  assert.equal(isRoundClosed(c), true);
});

test('PL: pot-limit max raise is pot + call', () => {
  const PL: Ruleset = { ...NL, bettingStructure: 'PL' };
  // pot starts empty; seat0 committedThisHand simulate a 10 pot via prior commit
  let c = ctx([seat(0, 100), seat(1, 100)], 0);
  c.seats[0]!.committedThisHand = 5;
  c.seats[1]!.committedThisHand = 5; // pot = 10
  c = applyAction(c, PL, { kind: 'bet', seat: 0, amount: 10 }); // pot now 20+...
  const l = legalActions(c, PL, 1);
  // toCall=10; potAfterCall = (10 + 10committedThisHand seat0 + 0) ... ensure raise present
  assert.ok(l.raise, 'PL offers a raise');
  assert.ok(l.raise!.max >= l.raise!.min);
});

test('FL: fixed bet/raise sizes and raise cap', () => {
  const FL: Ruleset = {
    ...NL,
    bettingStructure: 'FL',
    flSizing: { smallBet: 2, bigBet: 4, maxRaisesPerStreet: 3 },
  };
  let c = openRound(ctx([seat(0, 100), seat(1, 100)], 0), 0, 'small');
  const open = legalActions(c, FL, 0);
  assert.deepEqual(open.bet, { min: 2, max: 2 }); // fixed small bet
  c = applyAction(c, FL, { kind: 'bet', seat: 0, amount: 2 });
  const r = legalActions(c, FL, 1);
  assert.deepEqual(r.raise, { min: 4, max: 4 }); // raise-to fixed at +smallBet
});
