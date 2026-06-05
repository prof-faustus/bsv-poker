/**
 * In-tree BSV regtest node DAEMON — a thin TCP server around `RegtestNode`
 * (`@bsv-poker/adapters/regtest-node`). STANDALONE: this is the project's OWN node, run as a
 * subprocess exactly the way the in-tree `relay-go`/`indexer-go` services are. It replaces the
 * external `bonded-subsat-channel` python daemon so multi-process on-chain e2es (which share one
 * chain across processes via TCP) depend on NO external system.
 *
 * Protocol: newline-delimited JSON over TCP, matching `RealBsvNode` (`adapters/real-node.ts`):
 *   {cmd:'ping'}                                  -> {ok, pong}
 *   {cmd:'node.height'}                           -> {ok, height}
 *   {cmd:'node.generate', payout_pk_hex}          -> {ok, block_hash, txs, coinbase_txid}
 *   {cmd:'node.submit', raw_tx_hex}               -> {ok, reason, txid}
 *   {cmd:'node.outpoint', txid_hex, vout}         -> {ok, unspent, value}
 *   {cmd:'node.utxo_count'}                        -> {ok, count}
 *   {cmd:'status'}                                 -> {ok, height, mempool}
 *   {cmd:'shutdown'}                               -> {ok}  (then exits)
 *
 * SECURITY BOUNDARY: every inbound line is bounded-parsed (`safeJsonParse`); a malformed command is
 * a clean error reply, never a crash. The validating work is delegated to RegtestNode (which runs the
 * real interpreter and enforces nLockTime finality + replacement). Loopback only.
 *
 * Run: node tools/regtest-node-daemon.ts --port 8744
 */

import { createServer, type Socket } from 'node:net';
import { safeJsonParse } from '@bsv-poker/protocol-types';
import { RegtestNode } from '@bsv-poker/adapters/regtest-node';

const arg = (name: string, dflt: string): string => {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1]! : dflt;
};
const PORT = Number(arg('--port', '8744'));
const node = new RegtestNode();

async function handle(cmd: Record<string, unknown>): Promise<Record<string, unknown>> {
  switch (cmd.cmd) {
    case 'ping':
      return { ok: true, pong: true };
    case 'node.height':
      return { ok: true, height: await node.height() };
    case 'node.generate': {
      if (typeof cmd.payout_pk_hex !== 'string') return { ok: false, reason: 'payout_pk_hex required' };
      const r = await node.generateBlock(cmd.payout_pk_hex);
      return { ok: true, block_hash: r.blockHash, txs: r.txs, coinbase_txid: r.coinbaseTxid };
    }
    case 'node.submit': {
      if (typeof cmd.raw_tx_hex !== 'string') return { ok: false, reason: 'raw_tx_hex required', txid: '' };
      const r = await node.submitTx(cmd.raw_tx_hex);
      return { ok: r.ok, reason: r.reason, txid: r.txid };
    }
    case 'node.outpoint': {
      if (typeof cmd.txid_hex !== 'string' || typeof cmd.vout !== 'number') return { ok: false, reason: 'txid_hex+vout required' };
      const r = await node.outpointStatus(cmd.txid_hex, cmd.vout);
      return { ok: true, unspent: r.unspent, value: r.value };
    }
    case 'node.utxo_count':
      return { ok: true, count: await node.utxoCount() };
    case 'status':
      return { ...(await node.status()) };
    case 'shutdown':
      setTimeout(() => process.exit(0), 10);
      return { ok: true };
    default:
      return { ok: false, reason: `unknown cmd: ${String(cmd.cmd)}` };
  }
}

const MAX_LINE = 1 << 20; // a single command line is tiny; bound it (CWE-400)

const server = createServer((sock: Socket) => {
  let buf = '';
  sock.setEncoding('utf8');
  sock.on('data', (chunk: string) => {
    buf += chunk;
    if (buf.length > MAX_LINE) {
      sock.destroy();
      return;
    }
    let nl: number;
    while ((nl = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, nl);
      buf = buf.slice(nl + 1);
      const parsed = safeJsonParse(line, { maxBytes: MAX_LINE });
      if (!parsed.ok || parsed.value === null || typeof parsed.value !== 'object') {
        sock.write(JSON.stringify({ ok: false, reason: 'malformed command' }) + '\n');
        continue;
      }
      // Each command is independent; reply in order.
      void handle(parsed.value as Record<string, unknown>).then(
        (res) => sock.write(JSON.stringify(res) + '\n'),
        (e) => sock.write(JSON.stringify({ ok: false, reason: (e as Error).message }) + '\n'),
      );
    }
  });
  sock.on('error', () => {
    /* client reset — ignore */
  });
});

server.listen(PORT, '127.0.0.1', () => {
  console.error(`[regtest-node-daemon] in-tree node listening on 127.0.0.1:${PORT} (standalone)`);
});
