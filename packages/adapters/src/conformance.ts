/**
 * The single contract-conformance suite per contract (core §2.6, REQ-DEP-003). Each function
 * is run against BOTH the fake and the real adapter; both MUST pass, so the fake provably
 * matches the real contract and a green run against the fake cannot certify a wrong engine.
 *
 * These are implementation-agnostic behavioural checks; they throw on the first violation.
 */

import assert from 'node:assert/strict';
import type { BSContract, CTContract, OBContract, VAContract } from './contracts.ts';

const bytes = (...xs: number[]): Uint8Array => Uint8Array.from(xs);

export async function runCTConformance(ct: CTContract): Promise<void> {
  // entropy commit/reveal binds without disclosing (core §4.1, REQ-CRYPTO-002).
  const secret = bytes(1, 2, 3, 4);
  const commitment = await ct.entropyCommit(secret);
  assert.equal(typeof commitment, 'string');
  assert.ok(commitment.length > 0);
  assert.equal(await ct.entropyReveal(commitment, secret), true, 'correct reveal accepts');
  assert.equal(await ct.entropyReveal(commitment, bytes(9, 9)), false, 'wrong reveal rejects');

  // shuffle produces one combined key per card and a stable order commitment (INV-CT-1).
  const input = {
    deckId: 'deck-0',
    partyPubKeys: ['02aa', '03bb'],
    partyEntropy: [bytes(7, 7, 7, 7), bytes(8, 8, 8, 8)],
    deckSize: 52,
  };
  const r1 = await ct.runShuffle(input);
  assert.equal(r1.combinedKeys.length, 52, 'one combined key per card');
  assert.equal(new Set(r1.combinedKeys).size, 52, 'combined keys are distinct');
  const r2 = await ct.runShuffle(input);
  assert.equal(r1.orderCommitment, r2.orderCommitment, 'shuffle is deterministic in its inputs');
  assert.equal(r1.seed, r2.seed);

  // conceal/reveal opening (core §4.5/§4.6).
  const blind = bytes(5, 6, 7, 8);
  const cmt = await ct.conceal('deck-0', 17, 42, blind);
  assert.equal(await ct.verifyReveal(cmt, 42, blind), true, 'correct opening verifies');
  assert.equal(await ct.verifyReveal(cmt, 43, blind), false, 'wrong face fails');
  assert.equal(await ct.verifyReveal(cmt, 42, bytes(0)), false, 'wrong blind fails');
}

export async function runBSConformance(bs: BSContract): Promise<void> {
  const { txid, status } = await bs.nodeBroadcast('deadbeef');
  assert.equal(status, 'accepted');
  assert.equal(await bs.nodeOutpointStatus(txid, 0), 'unspent');
  assert.equal(await bs.nodeOutpointStatus('00'.repeat(32), 0), 'unknown');

  // A channel opens with the fixed 1-sat bond (INV-BS-2).
  const cid = await bs.channelOpen({
    participants: ['02aa', '03bb'],
    granularityK: 1000,
    bondSats: 1,
  });
  assert.ok(cid.length > 0);

  // Q* whole-satoshi reconciliation conserves total and writes no fractional output (INV-BS-1).
  const out = bs.reconcileQstar([1500, 2500, 1000], 1000);
  assert.ok(out.every((x) => Number.isInteger(x)), 'all outputs are whole satoshis');
  assert.equal(
    out.reduce((s, x) => s + x, 0),
    5,
    'total whole-satoshi conserved (5000 micro / k=1000)',
  );
}

export async function runVAConformance(va: VAContract): Promise<void> {
  assert.match(va.boundary, /never truth-at-origin/i, 'INV-VA-2 boundary surfaced');
  const records = ['r0', 'r1', 'r2', 'r3', 'r4'];
  for (let i = 0; i < records.length; i++) {
    const bundle = await va.merkleProve(records, i);
    assert.equal(await va.merkleVerify(bundle), true, `inclusion proof verifies for ${i}`);
    // tamper → fails
    const bad = { ...bundle, leaf: bundle.leaf.replace(/^./, (c) => (c === 'a' ? 'b' : 'a')) };
    assert.equal(await va.merkleVerify(bad), false, `tampered leaf fails for ${i}`);
  }
}

export async function runOBConformance(ob: OBContract): Promise<void> {
  const key = 'cafe1234';
  const wrapped = await ob.wrap(key, '02aa');
  assert.notEqual(wrapped, key, 'wrapped differs from raw key (never plaintext)');
  assert.equal(await ob.unwrap(wrapped, 'priv'), key, 'unwrap recovers the key');

  // Revocation = unspent expiring output (INV-OB-2).
  assert.equal(await ob.isRevoked('sess@100', 50), false, 'not revoked before expiry');
  assert.equal(await ob.isRevoked('sess@100', 150), true, 'revoked after expiry');

  // Threshold split returns n shares.
  const shares = await ob.thresholdSplit('00ff', 2, 3);
  assert.equal(shares.length, 3);
  assert.equal(new Set(shares).size, 3, 'shares are distinct');
  await assert.rejects(ob.thresholdSplit('00', 4, 3), /bad threshold/);
}
