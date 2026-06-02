/**
 * Omaha module tests — core §7.3.1, REQ-FSM-006 / REQ-POKER-005. Uses the §19.D Omaha vector
 * board `As Ks Qs 2s 7d` to prove the 2+3 constraint picks a DIFFERENT winner than naive
 * best-of-7, plus 4-hole deal, determinism, and fold-without-reveal.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseCard, type Card, type Ruleset, type Action } from '@bsv-poker/protocol-types';
import { bestHigh, bestOmaha, compareHigh } from '@bsv-poker/hand-eval';
import { createOmaha, PHASES, type OmahaState } from '../src/omaha.ts';

const PLO: Ruleset = {
  variant: 'omaha',
  bettingStructure: 'PL',
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

const seats = [
  { seat: 0, stack: 100 },
  { seat: 1, stack: 100 },
];

// Deal order (button-first, 4 rounds): seat0 = deck[0,2,4,6]; seat1 = deck[1,3,5,7];
// board = deck[8..12] = As Ks Qs 2s 7d (the §19.D Omaha vector board).
//   seat0 hole = Js 9h 4c 3d  → Omaha best = high card A K Q J 9 (Js 9h | As Ks Qs)
//   seat1 hole = 8c 8d 5c 5d  → Omaha best = two pair 8s & 5s with A board kicker (much worse?)
// Actually seat1 with 8c8d makes pair eights + board; let's verify seat0 (high card) loses or
// wins via the evaluators directly below — the asserted winner is whatever the oracle says.
function omahaDeck(): Card[] {
  const s0 = ['Js', '9h', '4c', '3d'].map(parseCard);
  const s1 = ['8c', '8d', '5c', '5d'].map(parseCard);
  const board = ['As', 'Ks', 'Qs', '2s', '7d'].map(parseCard);
  // interleave hole cards in deal order
  const head: Card[] = [];
  for (let k = 0; k < 4; k++) {
    head.push(s0[k]!);
    head.push(s1[k]!);
  }
  head.push(...board);
  const used = new Set(head);
  const rest: Card[] = [];
  for (let c = 0; c < 52; c++) if (!used.has(c)) rest.push(c);
  return [...head, ...rest];
}

test('4 hole cards are dealt per seat (REQ-FSM-006 override i)', () => {
  const m = createOmaha({ deck: omahaDeck() });
  const s = m.init(PLO, seats);
  assert.equal(s.hole[0]!.length, 4);
  assert.equal(s.hole[1]!.length, 4);
  assert.deepEqual([...s.hole[0]!], ['Js', '9h', '4c', '3d'].map(parseCard));
  assert.equal(s.phase, PHASES.BET_PREFLOP);
});

test('Omaha 2+3 constraint: showdown winner DIFFERS from naive best-of-7 (§19.D / REQ-POKER-005)', () => {
  const board = ['As', 'Ks', 'Qs', '2s', '7d'].map(parseCard);
  const h0 = ['Js', '9h', '4c', '3d'].map(parseCard);
  const h1 = ['8c', '8d', '5c', '5d'].map(parseCard);

  // Naive best-of-7 (Hold'em-style, all 4 hole + board) gives seat0 a spade FLUSH.
  const naive0 = bestHigh([...h0, ...board]).value;
  assert.equal(naive0.category, 5); // flush — the WRONG answer for Omaha (§19.D)

  // Correct Omaha (exactly 2 hole + 3 board) gives seat0 only high card A-K-Q-J-9.
  const omaha0 = bestOmaha(h0, board).value;
  const omaha1 = bestOmaha(h1, board).value;
  assert.equal(omaha0.category, 0); // high card — no flush possible (one spade in hand)

  // Now play the hand through the module and confirm the module uses the Omaha evaluator.
  const m = createOmaha({ deck: omahaDeck() });
  let s: OmahaState = m.init(PLO, seats);
  const play: Action[] = [
    { kind: 'call', seat: 0, amount: 1 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 }, // flop
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 }, // turn
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 }, // river
    { kind: 'check', seat: 0, amount: 0 },
  ];
  for (const a of play) s = m.apply(s, a);
  assert.equal(s.phase, PHASES.HAND_END);
  assert.equal(s.board.length, 5);

  // The module-decided winner must match the Omaha (2+3) comparison, NOT the naive one.
  const omahaWinner = compareHigh(omaha0, omaha1) > 0 ? 0 : 1;
  const naive1 = bestHigh([...h1, ...board]).value;
  const naiveWinner = compareHigh(naive0, naive1) > 0 ? 0 : 1;
  const payoutWinner = s.payouts.find((p) => p.amount > 0)!.seat;
  assert.equal(payoutWinner, omahaWinner);
  // Sanity: the two regimes pick different winners here (the whole point of §19.D).
  assert.notEqual(omahaWinner, naiveWinner);
});

test('fold without reveal ends the hand uncontested (P5)', () => {
  const m = createOmaha({ deck: omahaDeck() });
  let s = m.init(PLO, seats);
  s = m.apply(s, { kind: 'fold', seat: 0, amount: 0 });
  assert.equal(s.phase, PHASES.HAND_END);
  assert.equal(s.handComplete, true);
  assert.deepEqual([...s.payouts], [{ seat: 1, amount: 3 }]);
  assert.equal(s.board.length, 0);
});

test('determinism: replay yields byte-identical stateHash (P2)', () => {
  const play: Action[] = [
    { kind: 'call', seat: 0, amount: 1 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'bet', seat: 1, amount: 4 },
    { kind: 'call', seat: 0, amount: 4 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 0, amount: 0 },
  ];
  const run = (): OmahaState => {
    const m = createOmaha({ deck: omahaDeck() });
    let s = m.init(PLO, seats);
    for (const a of play) s = m.apply(s, a);
    return s;
  };
  const m = createOmaha({ deck: omahaDeck() });
  const h1 = m.stateHash(run());
  const h2 = m.stateHash(run());
  assert.equal(h1, h2);
  assert.equal(h1.length, 64);
});

// Omaha-8 hi-lo (REQ-FSM-007). Deal order (button-first, 4 rounds):
//   seat0 = As Js Kd Qd  → high: A-K-Q-J-4 SPADE FLUSH (As Js | 4s Ks Qs); no qualifying low.
//   seat1 = Ah 5h 6c 7d  → low: A-2-3-4-5 WHEEL (Ah 5h | 2c 3d 4s); high: 5-high straight.
//   board = 2c 3d 4s Ks Qs.  seat0 scoops HIGH (flush > straight); seat1 wins the LOW.
function hiLoDeck(): Card[] {
  const s0 = ['As', 'Js', 'Kd', 'Qd'].map(parseCard);
  const s1 = ['Ah', '5h', '6c', '7d'].map(parseCard);
  const board = ['2c', '3d', '4s', 'Ks', 'Qs'].map(parseCard);
  const head: Card[] = [];
  for (let k = 0; k < 4; k++) {
    head.push(s0[k]!);
    head.push(s1[k]!);
  }
  head.push(...board);
  const used = new Set(head);
  const rest: Card[] = [];
  for (let c = 0; c < 52; c++) if (!used.has(c)) rest.push(c);
  return [...head, ...rest];
}

const PLAY_TO_SHOWDOWN: Action[] = [
  { kind: 'call', seat: 0, amount: 1 },
  { kind: 'check', seat: 1, amount: 0 },
  { kind: 'check', seat: 1, amount: 0 },
  { kind: 'check', seat: 0, amount: 0 },
  { kind: 'check', seat: 1, amount: 0 },
  { kind: 'check', seat: 0, amount: 0 },
  { kind: 'check', seat: 1, amount: 0 },
  { kind: 'check', seat: 0, amount: 0 },
];

test('Omaha-8 hi-lo SPLITS the pot: high to the flush, low to the wheel (REQ-FSM-007)', () => {
  const hiLo: Ruleset = { ...PLO, hiLo: true };
  const m = createOmaha({ deck: hiLoDeck() });
  let s = m.init(hiLo, seats);
  for (const a of PLAY_TO_SHOWDOWN) s = m.apply(s, a);
  assert.equal(s.handComplete, true);
  // pot = 4; split → high half 2 (seat0 flush), low half 2 (seat1 wheel low)
  const byseat = new Map(s.payouts.map((p) => [p.seat, p.amount]));
  assert.equal(byseat.get(0), 2, 'seat0 takes the high half (flush)');
  assert.equal(byseat.get(1), 2, 'seat1 takes the low half (wheel)');
});

test('Omaha-8 with hiLo OFF: the high hand scoops the whole pot', () => {
  const m = createOmaha({ deck: hiLoDeck() });
  let s = m.init(PLO, seats); // hiLo false
  for (const a of PLAY_TO_SHOWDOWN) s = m.apply(s, a);
  assert.equal(s.handComplete, true);
  assert.deepEqual([...s.payouts], [{ seat: 0, amount: 4 }]); // flush scoops
});
