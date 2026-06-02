import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createSdk, validateRuleset, hashRuleset, type Player } from '../src/index.ts';
import { createSoftwareCustody } from '@bsv-poker/wallet-custody';
import type { Action, Ruleset } from '@bsv-poker/protocol-types';

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

function player(seat: number, stack: number, seed: number): Player {
  return {
    seat,
    stack,
    custody: createSoftwareCustody(Uint8Array.from(Array.from({ length: 32 }, (_, i) => (i + seed) % 251 || 1))),
    entropy: Uint8Array.from(Array.from({ length: 32 }, (_, i) => (i * seed + 13) % 251)),
  };
}

const PLAY: Action[] = [
  { kind: 'call', seat: 0, amount: 1 },
  { kind: 'check', seat: 1, amount: 0 },
  { kind: 'check', seat: 1, amount: 0 },
  { kind: 'check', seat: 0, amount: 0 },
  { kind: 'check', seat: 1, amount: 0 },
  { kind: 'check', seat: 0, amount: 0 },
  { kind: 'check', seat: 1, amount: 0 },
  { kind: 'check', seat: 0, amount: 0 },
];

test('validateRuleset accepts the Phase-1 ruleset and rejects bad ones', () => {
  assert.deepEqual(validateRuleset(NL), []);
  assert.ok(validateRuleset({ ...NL, seats: 1 }).length > 0);
  assert.ok(validateRuleset({ ...NL, bettingStructure: 'FL' }).length > 0); // missing flSizing
  assert.equal(hashRuleset(NL).length, 64);
});

test('runHand wires entropy/shuffle/deal/betting/settlement into one hand (Phase 1)', () => {
  const sdk = createSdk();
  const players = [player(0, 100, 7), player(1, 100, 19)];
  const res = sdk.runHand(players, NL, PLAY);
  assert.equal(res.state.handComplete, true);
  assert.equal(res.state.board.length, 5);
  // entropy reveals recorded in canonical party order
  assert.equal(res.transcript.entropy.length, 2);
  assert.equal(res.transcript.partyOrder.length, 2);
  // the pot conserves: total awarded == total committed
  const awarded = res.state.pots.reduce((s, p) => s + p.amount, 0);
  assert.equal(awarded, 4);
  // settlement spend (N-of-N close-out) verified through the REAL interpreter
  assert.equal(res.settlementVerified, true);
});

test('deriveState replays the transcript to byte-identical state (REQ-DATA-003, P2)', () => {
  const sdk = createSdk();
  const players = [player(0, 100, 7), player(1, 100, 19)];
  const res = sdk.runHand(players, NL, PLAY);
  const replayed = sdk.deriveState(res.transcript);
  // re-derive must match the live state on the load-bearing fields
  assert.equal(replayed.handComplete, res.state.handComplete);
  assert.deepEqual(replayed.board, res.state.board);
  assert.deepEqual(
    replayed.seats.map((s) => [s.seat, s.stack]),
    res.state.seats.map((s) => [s.seat, s.stack]),
  );
});

test('a withheld entropy reveal is detectable (commit binds the value, core §4.1)', async () => {
  const sdk = createSdk();
  const e = Uint8Array.from([1, 2, 3, 4]);
  const commitment = await sdk.ct.entropyCommit(e);
  assert.equal(await sdk.ct.entropyReveal(commitment, e), true);
  assert.equal(await sdk.ct.entropyReveal(commitment, Uint8Array.from([9, 9])), false);
});
