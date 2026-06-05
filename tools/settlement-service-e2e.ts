/**
 * Human-path settlement bridge E2E. A node-side settlement service (holding the human's on-chain
 * key) is driven over HTTP exactly as the desktop UI would: GET /pubkey, then POST /settle when the
 * hand ends. A second peer (a relay co-signer, like the opponent) co-signs over the relay; the
 * service submits the N-of-N settlement to the node and it confirms on-chain. This is how the
 * browser-bound human client settles on-chain without ever signing in the webview.
 */

import { spawn, type ChildProcess } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { RealBsvNode } from '@bsv-poker/adapters/real-node';
import { RelayClient } from '@bsv-poker/app-services';
import { OP, genKeyPair, signPreimage, fairPlayCommitment, fundingLocking, type Script, type KeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex, type BranchBinding } from '@bsv-poker/protocol-types';
import { type Tx, type TxOutput, serializeTxWire, txidWire, sighashMessage } from '@bsv-poker/tx-builder';
import { coSignSettlement } from './settlement-coordinator.ts';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const NODE_PORT = Number(process.env.BSV_NODE_PORT ?? 8748);
const RELAY_PORT = Number(process.env.BSV_RELAY_PORT ?? 8102);
const SVC_PORT = Number(process.env.BSV_SVC_PORT ?? 8202);
const RELAY_URL = `http://127.0.0.1:${RELAY_PORT}`;
const SUBSIDY = 5_000_000_000, FEE = 1000;
const BIND: BranchBinding = { gid: 'a1'.repeat(8), rulesetHash: 'b2'.repeat(32), round: 0, stateHash: 'c3'.repeat(32), actingSeat: -1, successorCommitment: '00'.repeat(32) };
const p2pkh = (pub: Uint8Array): Script => [OP.OP_DUP, OP.OP_HASH160, fairPlayCommitment(pub), OP.OP_EQUALVERIFY, OP.OP_CHECKSIG];
const sigT = (msg: Uint8Array, k: KeyPair): Uint8Array => signPreimage(msg, k.priv);
const procs: ChildProcess[] = [];

async function main(): Promise<void> {
  procs.push(spawn(process.execPath, [join(process.cwd(), 'tools/regtest-node-daemon.ts'), '--port', String(NODE_PORT)], { stdio: 'ignore' }));
  procs.push(spawn(join(ROOT, 'apps', 'relay-go', 'relay-go.exe'), ['-addr', `127.0.0.1:${RELAY_PORT}`], { stdio: 'ignore' }));
  procs.push(spawn('node', [join(ROOT, 'tools', 'settlement-service.ts'), '--port', String(SVC_PORT), '--node', String(NODE_PORT)], { cwd: ROOT, stdio: 'ignore' }));
  const node = new RealBsvNode('127.0.0.1', NODE_PORT);
  const tableId = `svc-${Date.now()}`;
  try {
    const dl = Date.now() + 30000;
    while (!(await node.ping().catch(() => false))) { if (Date.now() > dl) throw new Error('node down'); await new Promise((r) => setTimeout(r, 400)); }
    while (!(await new RelayClient(RELAY_URL).health().catch(() => false))) { if (Date.now() > dl) throw new Error('relay down'); await new Promise((r) => setTimeout(r, 300)); }
    await new RelayClient(RELAY_URL).createTable(tableId, 'svc');
    let pubA = '';
    for (;;) { try { pubA = ((await (await fetch(`http://127.0.0.1:${SVC_PORT}/pubkey`)).json()) as { pub: string }).pub; break; } catch { if (Date.now() > dl) throw new Error('service down'); await new Promise((r) => setTimeout(r, 300)); } }

    // Player A = the service (human, node-side custody). Player B = the relay co-signer (opponent).
    const keyB = genKeyPair();
    const ocPubsHex = [pubA, bytesToHex(keyB.pubCompressed)];
    const funder = genKeyPair();
    const escrow = 200_000_000;
    const cb = await node.generateBlock(bytesToHex(funder.pubCompressed));
    const fundingScript = fundingLocking(BIND, ocPubsHex.map((h) => Uint8Array.from(Buffer.from(h, 'hex'))));
    const fundingTx: Tx = { version: 1, inputs: [{ prevTxid: cb.coinbaseTxid, vout: 0, sequence: 0xffffffff }], outputs: [{ satoshis: escrow, locking: fundingScript }, { satoshis: SUBSIDY - escrow - FEE, locking: p2pkh(funder.pubCompressed) }], nLockTime: 0 };
    const fs: Script = [sigT(sighashMessage(fundingTx, 0, p2pkh(funder.pubCompressed), SUBSIDY), funder), funder.pubCompressed];
    assert.equal((await node.submitTx(bytesToHex(serializeTxWire(fundingTx, [fs])))).ok, true, 'escrow funding');
    await node.generateBlock(bytesToHex(funder.pubCompressed));
    const fundingTxid = txidWire(fundingTx, [fs]);

    // Settlement: A wins the escrow. A's output is built against pubA (the service key).
    const outputs = [{ pubHex: pubA, sats: escrow - FEE }];

    console.log('[svc-e2e] UI → POST /settle on the node-side service; opponent co-signs over the relay…');
    const [svcResp] = await Promise.all([
      fetch(`http://127.0.0.1:${SVC_PORT}/settle`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ relayUrl: RELAY_URL, tableId, idx: 0, n: 2, fundingTxid, potValueSats: escrow, ocPubsHex, outputs, submit: true }) }).then((r) => r.json() as Promise<{ ok: boolean; txid: string | null }>),
      coSignSettlement({ relayUrl: RELAY_URL, tableId, idx: 1, myKey: keyB, settleTx: { version: 1, inputs: [{ prevTxid: fundingTxid, vout: 0, sequence: 0xffffffff }], outputs: outputs.map((o) => ({ satoshis: o.sats, locking: p2pkh(Uint8Array.from(Buffer.from(o.pubHex, 'hex'))) })) as TxOutput[], nLockTime: 0 }, fundingScript, potValue: escrow, n: 2, submit: false, submitTx: (raw) => node.submitTx(raw) }),
    ]);
    if (!svcResp.ok) console.error('[svc-e2e] service response:', JSON.stringify(svcResp));
    assert.equal(svcResp.ok, true, 'service settle failed');
    assert.ok(svcResp.txid, 'service returned no txid');

    await node.generateBlock(bytesToHex(funder.pubCompressed));
    assert.equal((await node.outpointStatus(fundingTxid, 0)).unspent, false, 'escrow consumed');
    const won = await node.outpointStatus(svcResp.txid!, 0);
    assert.equal(won.unspent, true, 'human payout confirmed on-chain');
    assert.equal(won.value, escrow - FEE, 'human receives the escrow');
    console.log(`[svc-e2e] human (node-side service) settled ON-CHAIN: ${won.value} confirmed; key never left the service`);
    console.log('\n[svc-e2e] PASS — the browser-bound human client settled on-chain via the node-side service (no signing in the webview).');
  } finally {
    await node.shutdown().catch(() => {});
    for (const p of procs) p.kill();
  }
}

main().then(() => process.exit(0), (e) => { console.error('[svc-e2e] FAIL:', (e as Error).message); for (const p of procs) p.kill(); process.exit(1); });
