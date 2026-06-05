/**
 * On-chain POKER settlement E2E (core §6.6) against the REAL BSV node. The platform's own poker
 * templates settle a hand as real confirmed transactions:
 *   1. mine a coinbase to a key;
 *   2. FUNDING tx: spend the coinbase into an N-of-N multisig "pot" output (the funding template,
 *      bound to gid+rulesetHash) — node accepts + mines;
 *   3. SETTLEMENT tx: spend the pot (the funding multisig) to the winner via the settlement
 *      unlocking — node accepts + mines;
 *   4. confirm: the pot is spent, the winner's output is a confirmed UTXO.
 * This shows the actual poker money-flow on-chain through the real interpreter.
 */

import assert from 'node:assert/strict';
import { RegtestNode } from '@bsv-poker/adapters/regtest-node';
import {
  OP,
  genKeyPair,
  signPreimage,
  fairPlayCommitment,
  fundingLocking,
  fundingUnlocking,
  type Script,
  type KeyPair,
} from '@bsv-poker/script-templates-ts';
import { bytesToHex, type BranchBinding } from '@bsv-poker/protocol-types';
import { type Tx, serializeTxWire, txidWire, sighashMessage } from '@bsv-poker/tx-builder';

const SUBSIDY = 5_000_000_000;

const BIND: BranchBinding = {
  gid: 'ab'.repeat(8),
  rulesetHash: 'cd'.repeat(32),
  round: 0,
  stateHash: 'ef'.repeat(32),
  actingSeat: -1,
  successorCommitment: '00'.repeat(32),
};

function p2pkh(pub: Uint8Array): Script {
  return [OP.OP_DUP, OP.OP_HASH160, fairPlayCommitment(pub), OP.OP_EQUALVERIFY, OP.OP_CHECKSIG];
}
function sigWithType(msg: Uint8Array, k: KeyPair): Uint8Array {
  return signPreimage(msg, k.priv);
}

async function main(): Promise<void> {
  const node = new RegtestNode();
  const k0 = genKeyPair(); // coinbase owner / funder
  const p0 = genKeyPair(); // player 0 (winner)
  const p1 = genKeyPair(); // player 1
  try {
    const dl = Date.now() + 30000;
    while (!(await node.ping().catch(() => false))) {
      if (Date.now() > dl) throw new Error('node did not start');
      await new Promise((r) => setTimeout(r, 400));
    }

    const cb = await node.generateBlock(bytesToHex(k0.pubCompressed));
    console.log(`[onchain-poker] coinbase ${cb.coinbaseTxid.slice(0, 16)}… = ${SUBSIDY}`);

    // 1) FUNDING: spend the coinbase into a 2-of-2 multisig pot (the funding template).
    const pot = SUBSIDY - 1000;
    const fundingScript = fundingLocking(BIND, [p0.pubCompressed, p1.pubCompressed]);
    const fundingTx: Tx = {
      version: 1,
      inputs: [{ prevTxid: cb.coinbaseTxid, vout: 0, sequence: 0xffffffff }],
      outputs: [{ satoshis: pot, locking: fundingScript }],
      nLockTime: 0,
    };
    const fundMsg = sighashMessage(fundingTx, 0, p2pkh(k0.pubCompressed), SUBSIDY);
    const fundSig: Script = [sigWithType(fundMsg, k0), k0.pubCompressed];
    const fundRaw = bytesToHex(serializeTxWire(fundingTx, [fundSig]));
    const fundRes = await node.submitTx(fundRaw);
    console.log(`[onchain-poker] funding submit → ok=${fundRes.ok} reason="${fundRes.reason}"`);
    assert.equal(fundRes.ok, true, `funding rejected: ${fundRes.reason}`);
    await node.generateBlock(bytesToHex(k0.pubCompressed));
    const fundingTxid = txidWire(fundingTx, [fundSig]);
    assert.equal((await node.outpointStatus(fundingTxid, 0)).unspent, true, 'pot funded');

    // 2) SETTLEMENT: spend the 2-of-2 pot to the winner (p0).
    const payout = pot - 1000;
    const settleTx: Tx = {
      version: 1,
      inputs: [{ prevTxid: fundingTxid, vout: 0, sequence: 0xffffffff }],
      outputs: [{ satoshis: payout, locking: p2pkh(p0.pubCompressed) }],
      nLockTime: 0,
    };
    const settleMsg = sighashMessage(settleTx, 0, fundingScript, pot);
    // N-of-N: both players sign the close-out (OP_0 dummy + sigs in pubkey order)
    const settleSig = fundingUnlocking([sigWithType(settleMsg, p0), sigWithType(settleMsg, p1)]);
    const settleRaw = bytesToHex(serializeTxWire(settleTx, [settleSig]));
    const settleRes = await node.submitTx(settleRaw);
    console.log(`[onchain-poker] settlement submit → ok=${settleRes.ok} reason="${settleRes.reason}"`);
    assert.equal(settleRes.ok, true, `settlement rejected: ${settleRes.reason}`);
    await node.generateBlock(bytesToHex(k0.pubCompressed));

    const settleTxid = txidWire(settleTx, [settleSig]);
    assert.equal((await node.outpointStatus(fundingTxid, 0)).unspent, false, 'pot consumed by settlement');
    const won = await node.outpointStatus(settleTxid, 0);
    assert.equal(won.unspent, true, 'winner output confirmed');
    assert.equal(won.value, payout, 'winner receives the pot');
    console.log(`[onchain-poker] winner (p0) confirmed UTXO = ${won.value}`);

    console.log('\n[onchain-poker] PASS — poker funding multisig → settlement settled ON-CHAIN through the real node.');
  } finally {
    await node.shutdown();
  }
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('[onchain-poker] FAIL:', (e as Error).message);
    process.exit(1);
  },
);
