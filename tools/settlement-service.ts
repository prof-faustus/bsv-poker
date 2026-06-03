/**
 * Node-side settlement service (the human-path bridge). A browser webview cannot sign secp256k1 or
 * speak the node's TCP protocol, so the desktop runs this small node-side service: it holds the
 * player's on-chain key (custody boundary, REQ-APP-101), exposes the public key, and on request
 * co-signs the N-of-N settlement over the relay and submits it to the node. The UI calls /settle
 * when a hand ends (after the human confirms the signing prompt). Same protocol the bot daemon uses.
 *
 *   node tools/settlement-service.ts --port 8200 --node 8744 --relay http://127.0.0.1:8091
 */

import { createServer } from 'node:http';
import { RealBsvNode } from '@bsv-poker/adapters/real-node';
import { OP, genKeyPair, fairPlayCommitment, fundingLocking, type Script } from '@bsv-poker/script-templates-ts';
import { bytesToHex, type BranchBinding } from '@bsv-poker/protocol-types';
import { type Tx, type TxOutput } from '@bsv-poker/tx-builder';
import { coSignSettlement } from './settlement-coordinator.ts';

function arg(name: string, fb?: string): string | undefined {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fb;
}
const HTTP_PORT = Number(arg('--port', '8200'));
const NODE_PORT = Number(arg('--node', '8744'));
const BIND: BranchBinding = { gid: 'a1'.repeat(8), rulesetHash: 'b2'.repeat(32), round: 0, stateHash: 'c3'.repeat(32), actingSeat: -1, successorCommitment: '00'.repeat(32) };
const p2pkh = (pub: Uint8Array): Script => [OP.OP_DUP, OP.OP_HASH160, fairPlayCommitment(pub), OP.OP_EQUALVERIFY, OP.OP_CHECKSIG];
const hex = (h: string): Uint8Array => Uint8Array.from(Buffer.from(h, 'hex'));

const myKey = genKeyPair(); // the player's on-chain key — never leaves this service (custody boundary)
const node = new RealBsvNode('127.0.0.1', NODE_PORT);

interface SettleReq {
  relayUrl: string;
  tableId: string;
  idx: number;
  n: number;
  fundingTxid: string;
  fundingVout?: number;
  potValueSats: number;
  ocPubsHex: string[]; // all seated players' on-chain pubkeys, in seat order
  outputs: { pubHex: string; sats: number }[];
  submit: boolean;
}

function readBody(req: import('node:http').IncomingMessage): Promise<string> {
  return new Promise((resolve) => { let b = ''; req.on('data', (d) => (b += d)); req.on('end', () => resolve(b)); });
}

createServer((req, res) => {
  void (async () => {
    try {
      if (req.method === 'GET' && req.url === '/pubkey') {
        res.writeHead(200, { 'content-type': 'application/json', 'access-control-allow-origin': '*' });
        res.end(JSON.stringify({ pub: bytesToHex(myKey.pubCompressed) }));
        return;
      }
      if (req.method === 'POST' && req.url === '/settle') {
        const r = JSON.parse(await readBody(req)) as SettleReq;
        const fundingScript = fundingLocking(BIND, r.ocPubsHex.map(hex));
        const outputs: TxOutput[] = r.outputs.map((o) => ({ satoshis: o.sats, locking: p2pkh(hex(o.pubHex)) }));
        const settleTx: Tx = { version: 1, inputs: [{ prevTxid: r.fundingTxid, vout: r.fundingVout ?? 0, sequence: 0xffffffff }], outputs, nLockTime: 0 };
        const out = await coSignSettlement({ relayUrl: r.relayUrl, tableId: r.tableId, idx: r.idx, myKey, settleTx, fundingScript, potValue: r.potValueSats, n: r.n, submit: r.submit, submitTx: (raw) => node.submitTx(raw) });
        res.writeHead(200, { 'content-type': 'application/json', 'access-control-allow-origin': '*' });
        res.end(JSON.stringify({ ok: true, collected: out.collected, txid: out.txid ?? null }));
        return;
      }
      res.writeHead(404); res.end('not found');
    } catch (e) {
      res.writeHead(500, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ ok: false, error: (e as Error).message }));
    }
  })();
}).listen(HTTP_PORT, '127.0.0.1', () => console.log(`[settle-svc] node-side settlement service on http://127.0.0.1:${HTTP_PORT} (pub ${bytesToHex(myKey.pubCompressed).slice(0, 16)}…)`));
