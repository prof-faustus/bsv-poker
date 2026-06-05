/**
 * Full on-chain submit-and-confirm E2E (core §6, §8.4) against the REAL embedded BSV node. The
 * platform mines a coinbase to a key it controls, then BUILDS + SIGNS a real P2PKH spend (BIP-143
 * sighash) entirely in TypeScript, submits it — the node ACCEPTS it into the mempool through its
 * real Script interpreter — mines it, and the platform observes the confirmation via the UTXO
 * RPC (coinbase now spent, the new output unspent). This is a genuine on-chain transaction.
 */

import assert from 'node:assert/strict';
import { RegtestNode } from '@bsv-poker/adapters/regtest-node';
import { OP, genKeyPair, signPreimage, fairPlayCommitment, type Script } from '@bsv-poker/script-templates-ts';
import { bytesToHex } from '@bsv-poker/protocol-types';
import { type Tx, serializeTxWire, txidWire, sighashMessage } from '@bsv-poker/tx-builder';

const SUBSIDY = 5_000_000_000;

function p2pkh(pubCompressed: Uint8Array): Script {
  return [OP.OP_DUP, OP.OP_HASH160, fairPlayCommitment(pubCompressed), OP.OP_EQUALVERIFY, OP.OP_CHECKSIG];
}

async function main(): Promise<void> {
  const node = new RegtestNode();
  const k = genKeyPair();
  const payoutPub = bytesToHex(k.pubCompressed);
  try {
    const dl = Date.now() + 30000;
    while (!(await node.ping().catch(() => false))) {
      if (Date.now() > dl) throw new Error('node did not start');
      await new Promise((r) => setTimeout(r, 400));
    }

    console.log('[onchain-spend] mining a coinbase to the platform key…');
    const block = await node.generateBlock(payoutPub);
    const coinbaseTxid = block.coinbaseTxid;
    const before = await node.outpointStatus(coinbaseTxid, 0);
    assert.equal(before.unspent, true);
    console.log(`[onchain-spend] coinbase ${coinbaseTxid.slice(0, 16)}…:0 value=${before.value}`);

    // Build a real P2PKH spend of the coinbase, paying value-1000 back to the same key.
    const scriptCode = p2pkh(k.pubCompressed); // the coinbase's P2PKH scriptPubKey
    const spend: Tx = {
      version: 1,
      inputs: [{ prevTxid: coinbaseTxid, vout: 0, sequence: 0xffffffff }],
      outputs: [{ satoshis: SUBSIDY - 1000, locking: p2pkh(k.pubCompressed) }],
      nLockTime: 0,
    };
    // sign the BIP-143 sighash; the scriptSig sig carries the sighash-type byte (ALL|FORKID).
    const msg = sighashMessage(spend, 0, scriptCode, SUBSIDY);
    const der = signPreimage(msg, k.priv);
    const sigWithType = der;
    const scriptSig: Script = [sigWithType, k.pubCompressed];
    const rawTx = bytesToHex(serializeTxWire(spend, [scriptSig]));
    const spendTxid = txidWire(spend, [scriptSig]);

    console.log('[onchain-spend] submitting the signed spend to the node…');
    const res = await node.submitTx(rawTx);
    console.log(`[onchain-spend] submit → ok=${res.ok} reason="${res.reason}" txid=${res.txid.slice(0, 16)}…`);
    assert.equal(res.ok, true, `node must accept the platform-signed spend (reason: ${res.reason})`);

    console.log('[onchain-spend] mining a block to confirm…');
    await node.generateBlock(payoutPub);
    const coinbaseAfter = await node.outpointStatus(coinbaseTxid, 0);
    const newOut = await node.outpointStatus(spendTxid, 0);
    console.log(`[onchain-spend] coinbase now spent=${!coinbaseAfter.unspent}; new output unspent=${newOut.unspent} value=${newOut.value}`);
    assert.equal(coinbaseAfter.unspent, false, 'coinbase consumed by the confirmed spend');
    assert.equal(newOut.unspent, true, 'the spend output is now a confirmed UTXO');
    assert.equal(newOut.value, SUBSIDY - 1000, 'output value');

    console.log('\n[onchain-spend] PASS — platform built+signed a real tx; the node accepted, mined, and confirmed it.');
  } finally {
    await node.shutdown();
  }
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('[onchain-spend] FAIL:', (e as Error).message);
    process.exit(1);
  },
);
