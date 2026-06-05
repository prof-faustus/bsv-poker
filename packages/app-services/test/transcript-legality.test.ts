/**
 * Legality-validating indexer layer (audit #30): `validateHandLegality` replays an authenticated
 * transcript through the ONE canonical engine and rejects anything that is not a LEGAL game — an
 * illegal action, a forged/extra action record, or a reveal that does not match its commit. Positive
 * (a real legal hand validates) + negatives (each rejection path).
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { validateHandLegality } from '../src/transcript.ts';
import { createGameModule } from '../src/game-registry.ts';
import { universalBot, offlineRuleset } from '../src/offline.ts';
import { deckFromEntropies } from '../src/mp-shuffle.ts';
import { sha256, bytesToHex } from '@bsv-poker/protocol-types';
import type { TxRecord } from '../src/network.ts';
import type { TablePlayer } from '../src/interactive-client.ts';

interface Env { t: string; seat: number; hand: number; c?: string; r?: string; kind?: string; amount?: number }
const rec = (e: Env): TxRecord => ({ txid: `tx${Math.random().toString(36).slice(2)}`, class: 'protocol', tableId: 't', raw: btoa(JSON.stringify(e)) });

const RULESET = offlineRuleset('holdem', 2);
const SEATS: TablePlayer[] = [{ seat: 0, stack: 100 }, { seat: 1, stack: 100 }];

/** Drive a real legal heads-up hand through the engine and capture its commit/reveal/action envelopes. */
function legalTranscript(): { envs: Env[]; actions: Env[] } {
  const e0 = new Uint8Array(randomBytes(32));
  const e1 = new Uint8Array(randomBytes(32));
  const deck = deckFromEntropies([e0, e1]);
  const m = createGameModule('holdem', deck, 0);
  let state = m.init(RULESET, SEATS.map((s) => ({ seat: s.seat, stack: s.stack })));
  const envs: Env[] = [
    { t: 'commit', seat: 0, hand: 0, c: bytesToHex(sha256(e0)) },
    { t: 'commit', seat: 1, hand: 0, c: bytesToHex(sha256(e1)) },
    { t: 'reveal', seat: 0, hand: 0, r: bytesToHex(e0) },
    { t: 'reveal', seat: 1, hand: 0, r: bytesToHex(e1) },
  ];
  const actions: Env[] = [];
  for (let g = 0; g < 500 && !state.handComplete; g++) {
    const toAct = state.betting.toAct ?? state.drawToAct ?? null;
    if (toAct === null) break;
    const a = universalBot(m.getLegalActions(state, toAct), toAct);
    actions.push({ t: 'action', seat: toAct, hand: 0, kind: a.kind, amount: a.amount ?? 0 });
    state = m.apply(state, a);
  }
  return { envs, actions };
}

test('a real legal heads-up transcript validates (audit #30)', () => {
  const { envs, actions } = legalTranscript();
  const v = validateHandLegality([...envs, ...actions].map(rec), RULESET, SEATS);
  assert.equal(v.valid, true, `legal transcript must validate: ${v.reason}`);
  assert.ok(v.stateHash, 'a valid verdict carries the reconstructed state hash');
});

test('an ILLEGAL action (over-stack bet) is rejected (audit #30)', () => {
  const { envs, actions } = legalTranscript();
  assert.ok(actions.length > 0, 'need at least one action to tamper');
  // Replace the first actor's first move with a bet far exceeding the stack — illegal for any state.
  const tampered = actions.map((a, i) => (i === 0 ? { ...a, kind: 'bet', amount: 10_000_000 } : a));
  const v = validateHandLegality([...envs, ...tampered].map(rec), RULESET, SEATS);
  assert.equal(v.valid, false, 'an over-stack bet must be rejected');
  assert.match(v.reason ?? '', /illegal action/);
});

test('a FORGED/extra action record is rejected as unconsumed (audit #30)', () => {
  const { envs, actions } = legalTranscript();
  const forged: Env = { t: 'action', seat: 0, hand: 0, kind: 'check', amount: 0 };
  const v = validateHandLegality([...envs, ...actions, forged].map(rec), RULESET, SEATS);
  assert.equal(v.valid, false, 'an extra/forged action must be rejected');
  assert.match(v.reason ?? '', /forged|unconsumed|incomplete/);
});

test('a reveal that does not match its commit is rejected (audit #30)', () => {
  const { envs, actions } = legalTranscript();
  const corrupted = envs.map((e) => (e.t === 'commit' && e.seat === 0 ? { ...e, c: 'ff'.repeat(32) } : e));
  const v = validateHandLegality([...corrupted, ...actions].map(rec), RULESET, SEATS);
  assert.equal(v.valid, false, 'a reveal-vs-commit mismatch must be rejected');
  assert.match(v.reason ?? '', /commit/);
});
