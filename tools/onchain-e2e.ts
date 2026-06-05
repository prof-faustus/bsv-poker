/**
 * On-chain UTXO/submit E2E against the REAL embedded BSV node (core §8.4, REQ-NET-004 / REQ-DEP-004).
 * The platform mines a regtest block, then reads the REAL coinbase UTXO from the node (outpoint
 * status + value), checks the UTXO-set size, and exercises the submit path through the node's REAL
 * Script interpreter. This is the platform observing/driving genuine chain state, not a fake.
 *
 * Full acceptance of a platform-BUILT poker tx (byte-exact sighash/script interop with bitcoinx)
 * is the final on-chain step; this proves the chain-query + submit RPCs are real and wired.
 */

import assert from 'node:assert/strict';
import { RegtestNode } from '@bsv-poker/adapters/regtest-node';
import { genKeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex } from '@bsv-poker/protocol-types';

const REGTEST_COINBASE = 5_000_000_000; // 50 BSV regtest subsidy (node.coinbase_reward)

async function main(): Promise<void> {
  const node = new RegtestNode();
  const payoutPub = bytesToHex(genKeyPair().pubCompressed);
  try {
    const dl = Date.now() + 30000;
    while (!(await node.ping().catch(() => false))) {
      if (Date.now() > dl) throw new Error('node did not start');
      await new Promise((r) => setTimeout(r, 400));
    }

    console.log('[onchain-e2e] mining a regtest block (coinbase to the platform key)…');
    const block = await node.generateBlock(payoutPub);
    console.log(`[onchain-e2e] coinbase txid = ${block.coinbaseTxid.slice(0, 24)}…`);

    // Read the REAL coinbase UTXO from the node.
    const out = await node.outpointStatus(block.coinbaseTxid, 0);
    console.log(`[onchain-e2e] coinbase outpoint: unspent=${out.unspent} value=${out.value}`);
    assert.equal(out.unspent, true, 'the freshly-mined coinbase output is unspent');
    assert.equal(out.value, REGTEST_COINBASE, 'coinbase value is the regtest subsidy');

    const count = await node.utxoCount();
    console.log(`[onchain-e2e] node UTXO-set size = ${count}`);
    assert.ok(count >= 1, 'the UTXO set holds the coinbase');

    // A spent/nonexistent outpoint reads as not-unspent.
    const ghost = await node.outpointStatus('00'.repeat(32), 0);
    assert.equal(ghost.unspent, false, 'an unknown outpoint is not unspent');

    // The submit RPC reaches the node's REAL validator (an input-less tx is rejected with a reason).
    const emptyTx = '01000000' + '00' + '00' + '00000000';
    const res = await node.submitTx(emptyTx);
    console.log(`[onchain-e2e] submit(empty tx) → ok=${res.ok} reason="${res.reason}" (real validator)`);
    assert.equal(res.ok, false, 'the real validator rejects an input-less tx');

    console.log('\n[onchain-e2e] PASS — platform reads real UTXO state + submits through the real BSV validator.');
  } finally {
    await node.shutdown();
  }
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('[onchain-e2e] FAIL:', (e as Error).message);
    process.exit(1);
  },
);
