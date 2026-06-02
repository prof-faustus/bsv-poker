import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseCard, type Card, type Ruleset, type Action } from '@bsv-poker/protocol-types';
import { createHoldem, PHASES, type HoldemState } from '../src/holdem.ts';

const NL: Ruleset = {
  variant: 'holdem',
  bettingStructure: 'NL',
  forcedBetModel: 'blinds',
  seats: 2,
  blinds: { smallBlind: 1, bigBlind: 2, ante: 0, bringIn: 0 },
  minBuyIn: 100,
  maxBuyIn: 200,
  timeouts: { decisionMs: 30000, recoveryMs: 120000 },
  signingMode: 'A',
  currency: 'play-regtest',
  suitTiebreakHouseRule: false,
  hiLo: false,
};

// Build a 52-card deck whose first 9 positions force a known heads-up hand:
//   deal order (button-first): seat0=deck[0],deck[2]; seat1=deck[1],deck[3]; board=deck[4..8].
//   seat0 = As Ah (AA), seat1 = Ks Kh (KK), board = Qd Jc 9h 4s 3h → seat0 wins (pair aces).
function fixedDeck(): Card[] {
  const head = ['As', 'Ks', 'Ah', 'Kh', 'Qd', 'Jc', '9h', '4s', '3h'].map(parseCard);
  const used = new Set(head);
  const rest: Card[] = [];
  for (let c = 0; c < 52; c++) if (!used.has(c)) rest.push(c);
  return [...head, ...rest];
}

const seats = [
  { seat: 0, stack: 100 },
  { seat: 1, stack: 100 },
];

test('full heads-up hand to showdown: AA beats KK; pot settled, stacks updated', () => {
  const m = createHoldem({ deck: fixedDeck() });
  let s: HoldemState = m.init(NL, seats);
  assert.equal(s.phase, PHASES.BET_PREFLOP);
  assert.equal(s.betting.toAct, 0); // button/SB acts first preflop
  assert.equal(s.betting.betToCall, 2);

  const play: Action[] = [
    { kind: 'call', seat: 0, amount: 1 }, // SB completes (1 already posted)
    { kind: 'check', seat: 1, amount: 0 }, // BB checks → preflop closes
    { kind: 'check', seat: 1, amount: 0 }, // flop: non-button first
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 }, // turn
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 }, // river
    { kind: 'check', seat: 0, amount: 0 },
  ];
  for (const a of play) s = m.apply(s, a);

  assert.equal(s.phase, PHASES.HAND_END);
  assert.equal(s.handComplete, true);
  assert.equal(s.board.length, 5);
  // pot = 2 + 2 = 4 to seat 0
  assert.deepEqual(
    [...s.payouts].sort((a, b) => a.seat - b.seat),
    [{ seat: 0, amount: 4 }],
  );
  assert.equal(s.seats.find((x) => x.seat === 0)!.stack, 102);
  assert.equal(s.seats.find((x) => x.seat === 1)!.stack, 98);
});

test('fold without reveal ends the hand uncontested (P5)', () => {
  const m = createHoldem({ deck: fixedDeck() });
  let s = m.init(NL, seats);
  s = m.apply(s, { kind: 'fold', seat: 0, amount: 0 }); // SB folds preflop
  assert.equal(s.phase, PHASES.HAND_END);
  assert.equal(s.handComplete, true);
  // pot = SB(1) + BB(2) = 3 to seat 1; no board revealed
  assert.deepEqual([...s.payouts], [{ seat: 1, amount: 3 }]);
  assert.equal(s.seats.find((x) => x.seat === 1)!.stack, 101);
  assert.equal(s.seats.find((x) => x.seat === 0)!.stack, 99);
  assert.equal(s.board.length, 0); // fold without reveal
});

test('determinism: replaying the same actions yields byte-identical state (P2)', () => {
  const play: Action[] = [
    { kind: 'call', seat: 0, amount: 1 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'bet', seat: 1, amount: 4 }, // flop bet
    { kind: 'call', seat: 0, amount: 4 },
    { kind: 'check', seat: 1, amount: 0 }, // turn
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 }, // river
    { kind: 'check', seat: 0, amount: 0 },
  ];
  const run = (): HoldemState => {
    const m = createHoldem({ deck: fixedDeck() });
    let s = m.init(NL, seats);
    for (const a of play) s = m.apply(s, a);
    return s;
  };
  const m = createHoldem({ deck: fixedDeck() });
  const h1 = m.stateHash(run());
  const h2 = m.stateHash(run());
  assert.equal(h1, h2);
  assert.equal(h1.length, 64);
});

test('timeout-default is check when legal, else fold — never a forced wager (core §6.4)', () => {
  const m = createHoldem({ deck: fixedDeck() });
  let s = m.init(NL, seats);
  // SB facing the BB: default-on-timeout must be FOLD (cannot check facing a bet).
  const t1 = m.isTimeoutEligible(s, 0);
  assert.equal(t1!.seat, 0);
  assert.equal(t1!.defaultAction.kind, 'fold');
  // After SB completes and BB is to act with no bet to face: default is CHECK.
  s = m.apply(s, { kind: 'call', seat: 0, amount: 1 });
  const t2 = m.isTimeoutEligible(s, 0);
  assert.equal(t2!.seat, 1);
  assert.equal(t2!.defaultAction.kind, 'check');
});

test('multi-way (3-handed) all-in produces main + side pot; best hand sweeps (§19.B)', () => {
  // seat2 AA > seat1 KK > seat0 QQ on a blank board. Deal order button-first (0,1,2):
  //   c1: 0,1,2  c2: 3,4,5  board: 6..10
  const head = ['Qs', 'Ks', 'As', 'Qh', 'Kh', 'Ah', '2c', '7d', '9h', 'Jc', '4s'].map(parseCard);
  const used = new Set(head);
  const rest: Card[] = [];
  for (let c = 0; c < 52; c++) if (!used.has(c)) rest.push(c);
  const deck = [...head, ...rest];

  const m = createHoldem({ deck, buttonIndex: 0 });
  const seats3 = [
    { seat: 0, stack: 40 },
    { seat: 1, stack: 60 },
    { seat: 2, stack: 100 },
  ];
  let s = m.init(NL, seats3);
  // 3-handed: button=seat0 acts first preflop; SB=seat1, BB=seat2.
  assert.equal(s.betting.toAct, 0);
  s = m.apply(s, { kind: 'raise', seat: 0, amount: 40 }); // button all-in 40
  s = m.apply(s, { kind: 'raise', seat: 1, amount: 60 }); // SB all-in 60 (short)
  s = m.apply(s, { kind: 'call', seat: 2, amount: 58 }); // BB calls to 60 (2 already posted)
  assert.equal(s.handComplete, true);
  assert.equal(s.board.length, 5);
  // pots conserve and seat2 (AA) sweeps main(120) + side(40) = 160
  const total = s.pots.reduce((p, x) => p + x.amount, 0);
  assert.equal(total, 160);
  assert.equal(s.seats.find((x) => x.seat === 2)!.stack, 200);
  assert.equal(s.seats.find((x) => x.seat === 0)!.stack, 0);
  assert.equal(s.seats.find((x) => x.seat === 1)!.stack, 0);
});

test('getLegalActions only offers legal moves for the seat on the clock', () => {
  const m = createHoldem({ deck: fixedDeck() });
  const s = m.init(NL, seats);
  const legalSB = m.getLegalActions(s, 0);
  assert.equal(legalSB.check, false); // facing the big blind
  assert.deepEqual(legalSB.call, { amount: 1 });
  assert.ok(legalSB.raise);
  // not seat 1's turn
  assert.deepEqual(m.getLegalActions(s, 1), { check: false, fold: false });
});
