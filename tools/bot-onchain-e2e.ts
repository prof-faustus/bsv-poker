/**
 * Live hand → automatic on-chain settlement (the productized loop). Two networked peers play a full
 * hand over the relay, then — at hand-end — co-sign the N-of-N settlement of the table escrow to the
 * FINAL stacks over the relay socket and submit it to the real node. This is exactly what the bot
 * daemon does on each completed hand: play over the wire, then settle on-chain, no party acting alone.
 */

import { spawn, type ChildProcess } from 'node:child_process';
import assert from 'node:assert/strict';
import { RegtestNode } from '@bsv-poker/adapters/regtest-node';
import { RelayClient, NetworkedTableClient } from '@bsv-poker/app-services';
import { OP, genKeyPair, signPreimage, fairPlayCommitment, fundingLocking, type Script, type KeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex, type Action, type BranchBinding, type LegalActions, type Ruleset } from '@bsv-poker/protocol-types';
import { type Tx, type TxOutput, serializeTxWire, txidWire, sighashMessage } from '@bsv-poker/tx-builder';
import { coSignSettlement } from './settlement-coordinator.ts';

const RELAY_PORT = Number(process.env.BSV_RELAY_PORT ?? 8100);
const SUBSIDY = 5_000_000_000;
const SCALE = 1_000_000;
const FEE = 1000;
const STACK = 100;
const ROOT = process.cwd();
const BIND: BranchBinding = { gid: 'a1'.repeat(8), rulesetHash: 'b2'.repeat(32), round: 0, stateHash: 'c3'.repeat(32), actingSeat: -1, successorCommitment: '00'.repeat(32) };
const RULES: Ruleset = { variant: 'holdem', bettingStructure: 'NL', forcedBetModel: 'blinds', seats: 2, blinds: { smallBlind: 1, bigBlind: 2, ante: 0, bringIn: 0 }, minBuyIn: STACK, maxBuyIn: 200, timeouts: { decisionMs: 30000, recoveryMs: 120000 }, signingMode: 'A', currency: 'play-regtest', suitTiebreakHouseRule: false, hiLo: false };
const passive = (legal: LegalActions, seat: number): Action => legal.check ? { kind: 'check', seat, amount: 0 } : legal.call ? { kind: 'call', seat, amount: legal.call.amount } : { kind: 'fold', seat, amount: 0 };
const p2pkh = (pub: Uint8Array): Script => [OP.OP_DUP, OP.OP_HASH160, fairPlayCommitment(pub), OP.OP_EQUALVERIFY, OP.OP_CHECKSIG];
const sigT = (msg: Uint8Array, k: KeyPair): Uint8Array => signPreimage(msg, k.priv);

let relayProc: ChildProcess | null = null;

async function main(): Promise<void> {
  relayProc = spawn(`${ROOT}\\apps\\relay-go\\relay-go.exe`, ['-addr', `127.0.0.1:${RELAY_PORT}`], { stdio: 'ignore' });
  const node = new RegtestNode();
  const relayUrl = `http://127.0.0.1:${RELAY_PORT}`;
  const tableId = `bot-onchain-${Date.now()}`;
  try {
    const dl = Date.now() + 30000;
    while (!(await node.ping().catch(() => false))) { if (Date.now() > dl) throw new Error('node down'); await new Promise((r) => setTimeout(r, 400)); }
    while (!(await new RelayClient(relayUrl).health().catch(() => false))) { if (Date.now() > dl) throw new Error('relay down'); await new Promise((r) => setTimeout(r, 300)); }
    await new RelayClient(relayUrl).createTable(tableId, 'bot-onchain');

    // On-chain keys for the seated players + the escrow funded at table start.
    const players = [genKeyPair(), genKeyPair()];
    const funder = genKeyPair();
    const escrow = 2 * STACK * SCALE; // both buy-ins
    const cb = await node.generateBlock(bytesToHex(funder.pubCompressed));
    const fundingScript = fundingLocking(BIND, players.map((p) => p.pubCompressed));
    const fundingTx: Tx = { version: 1, inputs: [{ prevTxid: cb.coinbaseTxid, vout: 0, sequence: 0xffffffff }], outputs: [{ satoshis: escrow, locking: fundingScript }, { satoshis: SUBSIDY - escrow - FEE, locking: p2pkh(funder.pubCompressed) }], nLockTime: 0 };
    const fundSig: Script = [sigT(sighashMessage(fundingTx, 0, p2pkh(funder.pubCompressed), SUBSIDY), funder), funder.pubCompressed];
    assert.equal((await node.submitTx(bytesToHex(serializeTxWire(fundingTx, [fundSig])))).ok, true, 'escrow funding');
    await node.generateBlock(bytesToHex(funder.pubCompressed));
    const fundingTxid = txidWire(fundingTx, [fundSig]);

    // PLAY: two networked peers play a full hand over the relay.
    console.log('[bot-onchain] two peers playing a hand over the relay…');
    const seats = [{ seat: 0, stack: STACK }, { seat: 1, stack: STACK }];
    const mk = (seat: number, e: number) => new NetworkedTableClient({ relay: new RelayClient(relayUrl), tableId, mySeat: seat, seats, ruleset: RULES, entropy: Uint8Array.from(Array.from({ length: 32 }, (_, i) => (i * (seat * 6 + 7) + e) % 251)) });
    const [ra, rb] = await Promise.all([mk(0, 1).runHand(passive), mk(1, 5).runHand(passive)]);
    assert.equal(ra.stateHash, rb.stateHash, 'both peers agree on the final state');
    assert.equal(ra.state.handComplete, true);
    const finalStacks = ra.state.seats.map((s) => s.stack);
    console.log(`[bot-onchain] hand complete; final stacks ${finalStacks.join(' / ')} (sum ${finalStacks.reduce((a, b) => a + b, 0)})`);
    assert.equal(finalStacks.reduce((a, b) => a + b, 0), 2 * STACK, 'chips conserved across the hand');

    // SETTLE: distribute the escrow to the final stacks; both peers co-sign over the relay.
    const recips = finalStacks.map((c, i) => ({ i, sats: c * SCALE })).filter((r) => r.sats > 0).sort((a, b) => b.sats - a.sats);
    const outputs: TxOutput[] = recips.map((r, k) => ({ satoshis: k === 0 ? r.sats - FEE : r.sats, locking: p2pkh(players[r.i]!.pubCompressed) }));
    const settleTx: Tx = { version: 1, inputs: [{ prevTxid: fundingTxid, vout: 0, sequence: 0xffffffff }], outputs, nLockTime: 0 };

    console.log('[bot-onchain] peers co-signing the on-chain settlement over the relay…');
    const results = await Promise.all(
      players.map((k, i) => coSignSettlement({ relayUrl, tableId, idx: i, myKey: k, settleTx, fundingScript, potValue: escrow, n: 2, submit: i === 0, submitTx: (raw) => node.submitTx(raw) })),
    );
    await node.generateBlock(bytesToHex(funder.pubCompressed));

    assert.equal((await node.outpointStatus(fundingTxid, 0)).unspent, false, 'escrow consumed by the co-signed settlement');
    let confirmed = 0;
    for (let k = 0; k < outputs.length; k++) { const o = await node.outpointStatus(results[0]!.txid!, k); assert.equal(o.unspent, true, `payout ${k} confirmed`); confirmed += o.value; }
    assert.equal(confirmed, escrow - FEE, 'settled escrow conserved on-chain');
    console.log(`[bot-onchain] settled ON-CHAIN to final stacks: ${outputs.map((o) => o.satoshis).join(' / ')} (confirmed ${confirmed})`);

    console.log('\n[bot-onchain] PASS — a live networked hand auto-settled ON-CHAIN at hand-end (play over the wire → N-of-N co-sign over the relay → confirmed).');
  } finally {
    await node.shutdown();
    relayProc?.kill();
  }
}

main().then(() => process.exit(0), (e) => { console.error('[bot-onchain] FAIL:', (e as Error).message); relayProc?.kill(); process.exit(1); });
