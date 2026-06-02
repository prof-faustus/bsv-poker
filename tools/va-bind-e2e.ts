/**
 * VA binding E2E (REQ-DEP-004, core §17) — proves the poker audit trail is anchored by the REAL
 * verifiable-accounting-chain Merkle implementation, not a conformant fake.
 *
 * Builds per-hand settlement audit records, anchors them to a Merkle root via the real `@vaa/*`
 * library, proves inclusion of a record, verifies it through the real VA verifier, and shows that
 * tampering a record (a forged settlement) breaks inclusion against the anchored root.
 */

import assert from 'node:assert/strict';
import { RealVa } from '@bsv-poker/adapters/real-va';

const enc = (o: unknown): Uint8Array => new TextEncoder().encode(JSON.stringify(o));

async function main(): Promise<void> {
  const va = new RealVa();

  // Per-hand audit records the platform would publish (gid, winner, pot, settlement txid).
  const records = [
    enc({ hand: 1, gid: 'a1'.repeat(8), winnerSeat: 0, pot: 4999998000, txid: '0585821c'.repeat(8) }),
    enc({ hand: 2, gid: 'a1'.repeat(8), winnerSeat: 3, pot: 120000, txid: 'deadbeef'.repeat(8) }),
    enc({ hand: 3, gid: 'a1'.repeat(8), winnerSeat: 1, pot: 86000, txid: 'feedface'.repeat(8) }),
    enc({ hand: 4, gid: 'a1'.repeat(8), winnerSeat: 2, pot: 240000, txid: 'cafebabe'.repeat(8) }),
    enc({ hand: 5, gid: 'a1'.repeat(8), winnerSeat: 0, pot: 51000, txid: 'abad1dea'.repeat(8) }),
  ];

  const root = await va.anchor(records);
  console.log(`[va-bind] anchored ${records.length} hand records → real VA Merkle root ${root.slice(0, 20)}…`);
  assert.match(root, /^[0-9a-f]{64}$/, 'root is a 32-byte VA hash');

  // Prove + verify inclusion of hand #2 through the real library.
  const proof = await va.prove(records, 1);
  assert.equal(proof.rootHex, root, 'proof carries the anchored root');
  assert.equal(await va.verify(proof), true, 'real VA verifier confirms inclusion of hand #2');
  console.log(`[va-bind] hand #2 inclusion proof (${proof.siblingsHex.length} siblings) VERIFIED by real VA`);

  // Tamper: forge the settlement (different winner) → its leaf changes → inclusion fails.
  const forged = [...records];
  forged[1] = enc({ hand: 2, gid: 'a1'.repeat(8), winnerSeat: 9, pot: 120000, txid: 'deadbeef'.repeat(8) });
  const forgedProof = await va.prove(forged, 1);
  assert.notEqual(forgedProof.rootHex, root, 'forged record yields a different root');
  // The forged leaf against the HONEST anchored root must fail to verify.
  assert.equal(await va.verify({ ...forgedProof, rootHex: root }), false, 'forged settlement is rejected against the honest root');
  console.log('[va-bind] forged settlement (winnerSeat 3→9) REJECTED against the honest anchored root');

  console.log('\n[va-bind] PASS — poker audit trail anchored + verified by the REAL verifiable-accounting Merkle library (REQ-DEP-004).');
}

main().then(() => process.exit(0), (e) => { console.error('[va-bind] FAIL:', (e as Error).message); process.exit(1); });
