/**
 * RegtestNode — the in-tree, standalone BSV regtest node. Executable claims (INV-NODE-*).
 *
 * These prove the node enforces the consensus rules the on-chain layer relies on — script
 * verification through the REAL interpreter, value conservation, **nLockTime finality** (the maturity
 * gate), and sequence replacement — entirely in-tree, with no external process.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RegtestNode, p2pkhScript } from '../src/regtest-node.ts';
import {
  genKeyPair,
  signPreimage,
  type KeyPair,
} from '@bsv-poker/script-templates-ts';
import { type Tx, serializeTxWire, txidWire, sighashMessage } from '@bsv-poker/tx-builder';

const SUBSIDY = 5_000_000_000;
const FEE = 1000;

/** Spend the whole coinbase value (minus a fee) to `dest`, signed by `owner` (bare DER over the sighash). */
function spend(prevTxid: string, value: number, owner: KeyPair, destPub: Uint8Array, opts: { sequence?: number; nLockTime?: number } = {}): { raw: string; txid: string } {
  const tx: Tx = {
    version: 1,
    inputs: [{ prevTxid, vout: 0, sequence: opts.sequence ?? 0xffffffff }],
    outputs: [{ satoshis: value - FEE, locking: p2pkhScript(destPub) }],
    nLockTime: opts.nLockTime ?? 0,
  };
  const msg = sighashMessage(tx, 0, p2pkhScript(owner.pubCompressed), value);
  const ss = [signPreimage(msg, owner.priv), owner.pubCompressed];
  return { raw: bytesToHexLocal(serializeTxWire(tx, [ss])), txid: txidWire(tx, [ss]) };
}
function bytesToHexLocal(b: Uint8Array): string {
  let s = '';
  for (const x of b) s += x.toString(16).padStart(2, '0');
  return s;
}

async function fundedCoinbase(node: RegtestNode, k: KeyPair): Promise<string> {
  const cb = await node.generateBlock(bytesToHexLocal(k.pubCompressed));
  await node.generateBlock(bytesToHexLocal(k.pubCompressed)); // a second block (regtest convenience)
  return cb.coinbaseTxid;
}

// INV-NODE-1 (positive): a coinbase funds a P2PKH that the owner can spend; a wrong key fails in-script.
test('INV-NODE-1: P2PKH coinbase spend accepted; wrong key rejected in-script', async () => {
  const node = new RegtestNode();
  const k0 = genKeyPair();
  const k1 = genKeyPair();
  const cbid = await fundedCoinbase(node, k0);

  const wrong = spend(cbid, SUBSIDY, k1, k1.pubCompressed); // k1 cannot sign k0's coinbase
  const rWrong = await node.submitTx(wrong.raw);
  assert.equal(rWrong.ok, false);
  assert.match(rWrong.reason, /script rejected/);

  const good = spend(cbid, SUBSIDY, k0, k1.pubCompressed);
  const rGood = await node.submitTx(good.raw);
  assert.ok(rGood.ok, rGood.reason);
  await node.generateBlock(bytesToHexLocal(k0.pubCompressed));
  assert.equal((await node.outpointStatus(cbid, 0)).unspent, false, 'coinbase consumed');
  const out = await node.outpointStatus(good.txid, 0);
  assert.ok(out.unspent && out.value === SUBSIDY - FEE, 'spend output present + value conserved');
});

// INV-NODE-2 (NEGATIVE→POSITIVE): nLockTime FINALITY — a non-final tx with a future locktime is
// REJECTED before maturity and ACCEPTED once the chain reaches it. This is the maturity gate.
test('INV-NODE-2: nLockTime finality gate enforced (premature rejected, accepted at maturity)', async () => {
  const node = new RegtestNode();
  const k0 = genKeyPair();
  const cbid = await fundedCoinbase(node, k0);
  const maturity = (await node.height()) + 3;

  // non-final input (0xfffffffe) + future locktime → premature, must be rejected.
  const claim = spend(cbid, SUBSIDY, k0, k0.pubCompressed, { sequence: 0xfffffffe, nLockTime: maturity });
  const premature = await node.submitTx(claim.raw);
  assert.equal(premature.ok, false, 'premature non-final tx must be rejected');
  assert.match(premature.reason, /non-final|nLockTime/);

  // advance the chain to maturity, then the SAME tx is admissible.
  while ((await node.height()) < maturity) await node.generateBlock(bytesToHexLocal(k0.pubCompressed));
  const matured = await node.submitTx(claim.raw);
  assert.ok(matured.ok, `at maturity should be accepted: ${matured.reason}`);
});

// INV-NODE-3 (positive): a final tx (sequence 0xffffffff) ignores nLockTime — always admissible.
test('INV-NODE-3: a final-sequence input makes a future-locktime tx admissible', async () => {
  const node = new RegtestNode();
  const k0 = genKeyPair();
  const cbid = await fundedCoinbase(node, k0);
  const future = (await node.height()) + 1000;
  const finalTx = spend(cbid, SUBSIDY, k0, k0.pubCompressed, { sequence: 0xffffffff, nLockTime: future });
  assert.ok((await node.submitTx(finalTx.raw)).ok, 'final input → locktime ignored');
});

// INV-NODE-4 (positive): sequence replacement — a higher-sequence tx replaces a conflicting lower one.
test('INV-NODE-4: higher-sequence tx replaces a conflicting mempool tx', async () => {
  const node = new RegtestNode();
  const k0 = genKeyPair();
  const a = genKeyPair();
  const b = genKeyPair();
  const cbid = await fundedCoinbase(node, k0);

  const low = spend(cbid, SUBSIDY, k0, a.pubCompressed, { sequence: 1 });
  assert.ok((await node.submitTx(low.raw)).ok, 'low-seq admitted');
  const high = spend(cbid, SUBSIDY, k0, b.pubCompressed, { sequence: 2 });
  assert.ok((await node.submitTx(high.raw)).ok, 'high-seq replaces');
  // a lower-or-equal sequence is rejected as a replacement
  const equal = spend(cbid, SUBSIDY, k0, a.pubCompressed, { sequence: 2 });
  assert.equal((await node.submitTx(equal.raw)).ok, false, 'equal-seq must not replace');

  await node.generateBlock(bytesToHexLocal(k0.pubCompressed));
  // only the high-seq tx confirmed (paid b); the low-seq was superseded.
  assert.equal((await node.outpointStatus(high.txid, 0)).unspent, true, 'high-seq confirmed');
  assert.equal((await node.outpointStatus(low.txid, 0)).unspent, false, 'low-seq superseded, not mined');
});

// INV-NODE-5 (negative): a missing UTXO and a value-creating tx are rejected; submit never throws.
test('INV-NODE-5: unknown UTXO + value creation rejected; hostile hex never throws', async () => {
  const node = new RegtestNode();
  const k0 = genKeyPair();
  // spend a non-existent outpoint
  const ghost = spend('ab'.repeat(32), SUBSIDY, k0, k0.pubCompressed);
  assert.match((await node.submitTx(ghost.raw)).reason, /UTXO not found/);
  // hostile / malformed hex must be a clean rejection, never a throw
  for (const bad of ['', 'zz', 'deadbeef', '00'.repeat(5)]) {
    assert.doesNotThrow(async () => {
      const r = await node.submitTx(bad);
      assert.equal(r.ok, false);
    });
  }
});

// INV-NODE-6 (negative): an input-less non-coinbase tx is rejected (no value creation from nothing).
test('INV-NODE-6: input-less transactions are rejected', async () => {
  const node = new RegtestNode();
  // version(4) + vinCount(00) + voutCount(00) + nLockTime(4) = a 10-byte "empty" tx.
  const r = await node.submitTx('00000000' + '00' + '00' + '00000000');
  assert.equal(r.ok, false);
  assert.match(r.reason, /no inputs/);
});
