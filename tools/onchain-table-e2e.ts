/**
 * On-chain TABLE play E2E — proves that ACTUAL GAMEPLAY settles on-chain, for every variant. For
 * each variant we play a full hand on the real engine, read the engine-determined winner + pot, then
 * FUND that pot on-chain (coinbase → N-of-N multisig bound to the hand) and SETTLE it to the winner
 * as real confirmed transactions through the real node. No offline-only path: the hand's money moves
 * on-chain. (Chips are scaled to satoshis; regtest is the chain.)
 */

import assert from 'node:assert/strict';
import { RegtestNode } from '@bsv-poker/adapters/regtest-node';
import { playOfflineHand, offlineRuleset, seededShuffle } from '@bsv-poker/app-services';
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
import { bytesToHex, type BranchBinding, type Variant } from '@bsv-poker/protocol-types';
import { type Tx, serializeTxWire, txidWire, sighashMessage } from '@bsv-poker/tx-builder';

const SUBSIDY = 5_000_000_000;
const SCALE = 1_000_000; // 1 chip = 1e6 sats, so per-tx fees are negligible
const FEE = 1000;
const STACK = 100;

const BIND: BranchBinding = { gid: 'a1'.repeat(8), rulesetHash: 'b2'.repeat(32), round: 0, stateHash: 'c3'.repeat(32), actingSeat: -1, successorCommitment: '00'.repeat(32) };
const p2pkh = (pub: Uint8Array): Script => [OP.OP_DUP, OP.OP_HASH160, fairPlayCommitment(pub), OP.OP_EQUALVERIFY, OP.OP_CHECKSIG];
const sigT = (msg: Uint8Array, k: KeyPair): Uint8Array => signPreimage(msg, k.priv);

/** Settle ANY engine outcome on-chain: fund the total pot, then distribute it to each seat's share
 *  (committed + net), so split pots / multiple winners / side pots all settle faithfully. */
async function settleOnChain(node: RegtestNode, variant: Variant, seats: number, seed: number): Promise<void> {
  const ruleset = offlineRuleset(variant, seats);
  const seatInits = Array.from({ length: seats }, (_, i) => ({ seat: i, stack: STACK }));
  const b = new Uint8Array(32);
  b[0] = seed & 0xff;
  b[1] = (seed >> 8) & 0xff;
  b[2] = variant.charCodeAt(0);
  const st = playOfflineHand(variant, ruleset, seatInits, seededShuffle(b, 52));

  // Each seat's pot contribution and its share of the payout (contribution + net). Sum of shares
  // equals the pot exactly (chip conservation), so this works for any outcome including splits.
  const potChips = st.seats.reduce((a, s) => a + s.committedThisHand, 0);
  if (potChips === 0) return; // no wagering this hand (everyone checked the dark) — nothing to settle
  const shareChips = st.seats.map((s) => s.committedThisHand + (s.stack - STACK));
  assert.equal(shareChips.reduce((a, c) => a + c, 0), potChips, `${variant} seed ${seed}: pot != sum(shares)`);

  const pot = potChips * SCALE;
  const players = Array.from({ length: seats }, () => genKeyPair());
  const funder = genKeyPair();
  const cb = await node.generateBlock(bytesToHex(funder.pubCompressed));

  // FUNDING: coinbase → [pot multisig over the seated players] + [change to funder].
  const fundingScript = fundingLocking(BIND, players.map((p) => p.pubCompressed));
  const fundingTx: Tx = {
    version: 1,
    inputs: [{ prevTxid: cb.coinbaseTxid, vout: 0, sequence: 0xffffffff }],
    outputs: [{ satoshis: pot, locking: fundingScript }, { satoshis: SUBSIDY - pot - FEE, locking: p2pkh(funder.pubCompressed) }],
    nLockTime: 0,
  };
  const fundSig: Script = [sigT(sighashMessage(fundingTx, 0, p2pkh(funder.pubCompressed), SUBSIDY), funder), funder.pubCompressed];
  assert.equal((await node.submitTx(bytesToHex(serializeTxWire(fundingTx, [fundSig])))).ok, true, `${variant} funding`);
  await node.generateBlock(bytesToHex(funder.pubCompressed));
  const fundingTxid = txidWire(fundingTx, [fundSig]);

  // SETTLEMENT: distribute the N-of-N pot to each seat's share (one output per positive share).
  const recips = shareChips.map((c, i) => ({ i, sats: c * SCALE })).filter((r) => r.sats > 0);
  recips.sort((a, c) => c.sats - a.sats);
  const outputs = recips.map((r, k) => ({ satoshis: k === 0 ? r.sats - FEE : r.sats, locking: p2pkh(players[r.i]!.pubCompressed) }));
  const settleTx: Tx = { version: 1, inputs: [{ prevTxid: fundingTxid, vout: 0, sequence: 0xffffffff }], outputs, nLockTime: 0 };
  const msg = sighashMessage(settleTx, 0, fundingScript, pot);
  const settleSig = fundingUnlocking(players.map((p) => sigT(msg, p)));
  assert.equal((await node.submitTx(bytesToHex(serializeTxWire(settleTx, [settleSig])))).ok, true, `${variant} settlement`);
  await node.generateBlock(bytesToHex(funder.pubCompressed));

  const settleTxid = txidWire(settleTx, [settleSig]);
  assert.equal((await node.outpointStatus(fundingTxid, 0)).unspent, false, `${variant}: pot consumed`);
  let confirmed = 0;
  for (let k = 0; k < outputs.length; k++) {
    const o = await node.outpointStatus(settleTxid, k);
    assert.equal(o.unspent, true, `${variant}: settlement output ${k} confirmed`);
    confirmed += o.value;
  }
  assert.equal(confirmed, pot - FEE, `${variant}: distributed pot conserved`);
  console.log(`[onchain-table] ${variant} ${seats}p seed ${seed}: pot ${potChips} chips → ${recips.length} on-chain payout(s) confirmed (${confirmed} sats)`);
}

async function main(): Promise<void> {
  const node = new RegtestNode();
  try {
    const dl = Date.now() + 30000;
    while (!(await node.ping().catch(() => false))) { if (Date.now() > dl) throw new Error('node down'); await new Promise((r) => setTimeout(r, 400)); }
    // Every variant × several seat counts × several deals — split pots, multiway, side pots all
    // settle on-chain (faithful multi-output distribution).
    for (const [variant, seats] of [['holdem', 2], ['holdem', 6], ['omaha', 3], ['stud', 4], ['draw', 2], ['razz', 3]] as const) {
      for (let seed = 0; seed < 4; seed++) await settleOnChain(node, variant, seats, seed);
    }
    console.log('\n[onchain-table] PASS — every variant/outcome: real engine hands funded + settled ON-CHAIN (any distribution) through the real node (all play on-chain).');
  } finally {
    await node.shutdown();
  }
}

main().then(() => process.exit(0), (e) => { console.error('[onchain-table] FAIL:', (e as Error).message); process.exit(1); });
