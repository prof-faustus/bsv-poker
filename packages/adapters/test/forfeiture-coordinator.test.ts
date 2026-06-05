/**
 * ForfeitureCoordinator unit tests (audit #22) — the logic that turns a live `reveal` drop into an
 * on-chain bond forfeiture, against a fake node (the full on-chain proof is onchain-live-forfeit-e2e).
 * Positive + negative: only a reveal-drop with a matching bond is forfeited; commitment-mismatch,
 * unknown-seat, and non-reveal phases are ignored; flush respects the maturity (nLockTime) height.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ForfeitureCoordinator, type SeatBond, type ForfeitNode } from '../src/forfeiture-coordinator.ts';
import { p2pkhScript } from '../src/regtest-node.ts';
import { genKeyPair, bondRevealOrForfeitLocking } from '@bsv-poker/script-templates-ts';
import { sha256, bytesToHex, type BranchBinding } from '@bsv-poker/protocol-types';

const BIND: BranchBinding = { gid: 'a1'.repeat(8), rulesetHash: 'b2'.repeat(32), round: 0, stateHash: 'c3'.repeat(32), actingSeat: -1, successorCommitment: '00'.repeat(32) };

class FakeNode implements ForfeitNode {
  h = 0;
  submitted: string[] = [];
  async height(): Promise<number> { return this.h; }
  async submitTx(raw: string): Promise<{ ok: boolean; reason?: string }> { this.submitted.push(raw); return { ok: true }; }
}

function makeBond(seat: number): { bond: SeatBond; commitment: string } {
  const owner = genKeyPair();
  const bene = genKeyPair();
  const preimage = new Uint8Array(32).fill(seat + 1);
  const commitment = bytesToHex(sha256(preimage));
  const script = bondRevealOrForfeitLocking(BIND, sha256(preimage), owner.pubCompressed, bene.pubCompressed);
  return { bond: { txid: (seat + 100).toString(16).padStart(64, '0'), vout: 0, script, value: 1_000_000, commitment }, commitment };
}

function build(): { coord: ForfeitureCoordinator; node: FakeNode; commitment: string } {
  const node = new FakeNode();
  const bene = genKeyPair();
  const { bond, commitment } = makeBond(2);
  const bonds = new Map([[2, bond]]);
  const coord = new ForfeitureCoordinator({ node, beneficiary: bene, beneficiaryPayout: p2pkhScript(bene.pubCompressed), bonds });
  return { coord, node, commitment };
}

test('records a forfeiture only for a reveal-drop with a matching bond (audit #22)', () => {
  const { coord, commitment } = build();
  // NEGATIVE: an action-phase drop is not a forfeitable bond case.
  assert.equal(coord.record({ seat: 2, hand: 0, phase: 'action', deadlineHeight: 5 }), null);
  // NEGATIVE: an unknown seat has no bond.
  assert.equal(coord.record({ seat: 9, hand: 0, phase: 'reveal', deadlineHeight: 5, commitment }), null);
  // NEGATIVE: a commitment that does not match the bond is refused.
  assert.equal(coord.record({ seat: 2, hand: 0, phase: 'reveal', deadlineHeight: 5, commitment: 'ff'.repeat(32) }), null);
  // POSITIVE: a reveal-drop with the matching commitment is recorded.
  const plan = coord.record({ seat: 2, hand: 0, phase: 'reveal', deadlineHeight: 5, commitment });
  assert.ok(plan, 'a matching reveal-drop must produce a forfeiture plan');
  assert.equal(plan!.maturity, 5);
  assert.deepEqual(coord.pendingSeats(), [2]);
});

test('record is idempotent per seat (no duplicate forfeitures)', () => {
  const { coord, commitment } = build();
  const a = coord.record({ seat: 2, hand: 0, phase: 'reveal', deadlineHeight: 5, commitment });
  const b = coord.record({ seat: 2, hand: 0, phase: 'reveal', deadlineHeight: 5, commitment });
  assert.equal(a!.forfeitTxid, b!.forfeitTxid, 'a repeated drop must return the same plan');
  assert.equal(coord.pendingSeats().length, 1);
});

test('flush respects maturity: held back below the deadline, submitted at/after it (audit #22)', async () => {
  const { coord, node, commitment } = build();
  coord.record({ seat: 2, hand: 0, phase: 'reveal', deadlineHeight: 10, commitment });

  node.h = 9; // below maturity → the node would reject (nLockTime); the coordinator holds it back
  let res = await coord.flush();
  assert.equal(res[0]?.submitted, false, 'a forfeiture below maturity must not be submitted');
  assert.equal(node.submitted.length, 0, 'nothing should be sent to the node before maturity');
  assert.deepEqual(coord.pendingSeats(), [2], 'it stays pending for retry');

  node.h = 10; // at maturity → submit
  res = await coord.flush();
  assert.equal(res[0]?.submitted, true, 'at maturity the forfeiture is submitted');
  assert.equal(node.submitted.length, 1, 'exactly one forfeiture tx sent');
  assert.deepEqual(coord.pendingSeats(), [], 'a submitted forfeiture is cleared');
});
