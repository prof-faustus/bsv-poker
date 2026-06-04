import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createSessionAuth, verifySig, envelopeMessage } from '../src/session-auth.ts';
import { validateEnvelope } from '../src/message-validation.ts';

const TABLE = 'tbl-abc';

test('a seat-signed envelope verifies against the seat key (audit 1–3)', async () => {
  const seat0 = await createSessionAuth();
  const msg = envelopeMessage(TABLE, { t: 'action', seat: 0, hand: 0, kind: 'raise', amount: 50 });
  const sig = await seat0.sign(msg);
  assert.equal(await verifySig(seat0.pub, msg, sig), true);
});

test('FORGERY rejected: an action for seat 1 signed by an attacker is NOT valid under seat 1’s key', async () => {
  const seat1 = await createSessionAuth(); // the real player at seat 1
  const attacker = await createSessionAuth(); // anyone else on the relay
  const msg = envelopeMessage(TABLE, { t: 'action', seat: 1, hand: 0, kind: 'fold', amount: 0 });
  const forgedSig = await attacker.sign(msg);
  // The honest client verifies against the seat's REGISTERED key (seat1.pub) — the forgery fails.
  assert.equal(await verifySig(seat1.pub, msg, forgedSig), false);
});

test('UNSIGNED forged fold (the audit exploit) has no valid signature for the seat', async () => {
  const seat1 = await createSessionAuth();
  // {"t":"action","seat":1,"hand":0,"kind":"fold","amount":0} with no/garbage sig
  const raw = { t: 'action', seat: 1, hand: 0, kind: 'fold', amount: 0 } as const;
  assert.ok(validateEnvelope(raw), 'structurally valid…');
  assert.equal(await verifySig(seat1.pub, envelopeMessage(TABLE, raw), 'deadbeef'), false, '…but unsigned → rejected');
});

test('a signature does not replay across table / hand / seat (binding)', async () => {
  const k = await createSessionAuth();
  const sig = await k.sign(envelopeMessage(TABLE, { t: 'action', seat: 0, hand: 0, kind: 'check', amount: 0 }));
  assert.equal(await verifySig(k.pub, envelopeMessage('other-table', { t: 'action', seat: 0, hand: 0, kind: 'check', amount: 0 }), sig), false);
  assert.equal(await verifySig(k.pub, envelopeMessage(TABLE, { t: 'action', seat: 0, hand: 1, kind: 'check', amount: 0 }), sig), false);
  assert.equal(await verifySig(k.pub, envelopeMessage(TABLE, { t: 'action', seat: 2, hand: 0, kind: 'check', amount: 0 }), sig), false);
});

import { sessionAuthFromSeed, deriveSeatSeed } from '../src/session-auth.ts';

test('seat key derives DETERMINISTICALLY from a root (linked to the wallet master) — same root → same key', async () => {
  const root = new Uint8Array(32).fill(11);
  const a1 = await sessionAuthFromSeed(deriveSeatSeed(root));
  const a2 = await sessionAuthFromSeed(deriveSeatSeed(root));
  assert.equal(a1.pub, a2.pub, 'one root → one stable seat key');
  // sign+verify still works for a seed-derived key
  const m = envelopeMessage(TABLE, { t: 'commit', seat: 0, hand: 0, c: 'ab' });
  assert.equal(await verifySig(a1.pub, m, await a1.sign(m)), true);
});

test('different roots / different purposes derive different keys (domain separation)', async () => {
  const root = new Uint8Array(32).fill(11);
  const seat = await sessionAuthFromSeed(deriveSeatSeed(root, 'bsv-poker/seat-ed25519'));
  const other = await sessionAuthFromSeed(deriveSeatSeed(root, 'bsv-poker/wallet')); // a different purpose
  const diffRoot = await sessionAuthFromSeed(deriveSeatSeed(new Uint8Array(32).fill(22)));
  assert.notEqual(seat.pub, other.pub, 'seat vs wallet purpose → different keys');
  assert.notEqual(seat.pub, diffRoot.pub, 'different root → different key');
});

test('a signed action binds its prior state hash — cannot be replayed against a different state (audit 8)', async () => {
  const k = await createSessionAuth();
  const sig = await k.sign(envelopeMessage(TABLE, { t: 'action', seat: 0, hand: 0, kind: 'bet', amount: 10, prev: 'aa'.repeat(32) }));
  // same action, different prior state → signature does not verify
  assert.equal(await verifySig(k.pub, envelopeMessage(TABLE, { t: 'action', seat: 0, hand: 0, kind: 'bet', amount: 10, prev: 'bb'.repeat(32) }), sig), false);
  // same prev → verifies
  assert.equal(await verifySig(k.pub, envelopeMessage(TABLE, { t: 'action', seat: 0, hand: 0, kind: 'bet', amount: 10, prev: 'aa'.repeat(32) }), sig), true);
});
