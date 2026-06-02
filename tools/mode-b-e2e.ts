/**
 * Mode B (online threshold signing) E2E — core §4.3/§9.3, REQ-CRYPTO-008/REQ-TX-012, RT-02 F1.
 *
 * A t-of-n quorum from the REAL overlay-broadcast GG20 engine produces a standard ECDSA signature
 * under the group key WITHOUT ever reconstructing the group private key. We then prove that
 * signature is accepted by the platform's REAL Script interpreter's OP_CHECKSIG against the group
 * key — i.e. a Mode B settlement output (locked to the threshold group key) is spendable by the
 * threshold signature, exactly as a single-key spend would be. This closes the Mode B hole: no
 * party holds the whole key, yet the spend validates on the consensus verifier.
 *
 * Convention bridge: GG20 signs a 32-byte prehash directly; the interpreter's OP_CHECKSIG verifies
 * ECDSA over sha256(sighashPreimage). So we sign prehash = sha256(preimage) and hand the interpreter
 * `preimage`; the two digests coincide and the signature verifies.
 */

import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { RealOb } from '@bsv-poker/adapters/real-ob';
import { OP, evaluate, type Script } from '@bsv-poker/script-templates-ts';
import { sha256, bytesToHex } from '@bsv-poker/protocol-types';

async function main(): Promise<void> {
  const ob = new RealOb();

  for (const [t, n] of [[2, 3], [3, 5]] as const) {
    // A settlement "message" (stands in for the BIP-143 sighash preimage of a group-key payout).
    const preimage = Uint8Array.from(randomBytes(48));
    const prehash = sha256(preimage); // what OP_CHECKSIG (ECDSA-over-sha256) will effectively check

    const { groupKey, sig } = ob.thresholdSign(t, n, prehash);
    assert.equal(groupKey.length, 33, 'group key is a compressed point');

    // Mode B settlement lock = pay-to-(threshold group key); spent by the threshold signature.
    const locking: Script = [groupKey, OP.OP_CHECKSIG];
    const unlocking: Script = [sig];

    const ok = evaluate(unlocking, locking, { sighashPreimage: preimage }).ok;
    assert.equal(ok, true, `${t}-of-${n} threshold signature must satisfy OP_CHECKSIG under the group key`);

    // Tamper: any other message must fail — the threshold sig is bound to this preimage.
    const bad = evaluate(unlocking, locking, { sighashPreimage: Uint8Array.from(randomBytes(48)) }).ok;
    assert.equal(bad, false, 'a different message must not verify');

    console.log(`[mode-b] ${t}-of-${n}: threshold ECDSA under group ${bytesToHex(groupKey).slice(0, 18)}… ACCEPTED by OP_CHECKSIG; tamper rejected; key never reconstructed`);
  }

  console.log('\n[mode-b] PASS — Mode B online threshold signing: a t-of-n quorum (real GG20) signs a Mode B settlement spend that the real Script interpreter accepts under the group key (RT-02 F1 closed).');
}

main().then(() => process.exit(0), (e) => { console.error('[mode-b] FAIL:', (e as Error).message); process.exit(1); });
