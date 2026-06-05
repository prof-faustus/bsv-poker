/**
 * Real BSV node client (core §2.2 BS / §10.2, D6) — binds the platform's chain backend to the
 * **embedded BSV regtest node** shipped in the `bonded-subsat-channel` reference implementation
 * (the prof-faustus repo). It speaks that node daemon's newline-delimited JSON-over-TCP protocol
 * (cmd: ping / status / node.height / node.generate / shutdown).
 *
 * Node-side only (uses node:net); exported via the `@bsv-poker/adapters/real-node` subpath so it
 * never enters a browser bundle. This is the REAL adapter the conformance/integration tests run
 * against (REQ-DEP-004) — the node is run on the host (regtest only), driven from here.
 */

import { createConnection } from 'node:net';
import { safeJsonParse } from '@bsv-poker/protocol-types';

export interface NodeResponse {
  ok: boolean;
  [k: string]: unknown;
}

/** Hard cap on a single node response frame (CWE-400): the regtest daemon's replies are tiny. */
const MAX_NODE_FRAME = 1 << 20; // 1 MiB

export class RealBsvNode {
  private readonly host: string;
  private readonly port: number;
  constructor(host: string, port: number) {
    this.host = host;
    this.port = port;
  }

  private call(req: Record<string, unknown>, timeoutMs = 3000): Promise<NodeResponse> {
    return new Promise((resolve, reject) => {
      const sock = createConnection({ host: this.host, port: this.port });
      let buf = '';
      const done = (err: Error | null, val?: NodeResponse): void => {
        sock.destroy();
        if (err) reject(err);
        else resolve(val!);
      };
      sock.setTimeout(timeoutMs, () => done(new Error('node call timeout')));
      sock.on('error', (e) => done(e));
      sock.on('connect', () => sock.write(JSON.stringify(req) + '\n'));
      sock.on('data', (chunk) => {
        buf += chunk.toString('utf8');
        // Bound the accumulation: a peer that never sends a newline must not grow buf without limit.
        if (buf.length > MAX_NODE_FRAME) {
          done(new Error('node response exceeded frame bound'));
          return;
        }
        const nl = buf.indexOf('\n');
        if (nl >= 0) {
          const parsed = safeJsonParse(buf.slice(0, nl), { maxBytes: MAX_NODE_FRAME });
          if (parsed.ok && parsed.value !== null && typeof parsed.value === 'object') {
            done(null, parsed.value as NodeResponse);
          } else {
            done(new Error('node response was not a valid JSON object'));
          }
        }
      });
    });
  }

  async ping(): Promise<boolean> {
    const r = await this.call({ cmd: 'ping' });
    return r.ok === true && r.pong === true;
  }

  async height(): Promise<number> {
    const r = await this.call({ cmd: 'node.height' });
    if (!r.ok) throw new Error(`node.height failed: ${JSON.stringify(r)}`);
    return r.height as number;
  }

  /** Mine a regtest block paying out to `payoutPubHex`; returns the coinbase txid too. */
  async generateBlock(
    payoutPubHex: string,
  ): Promise<{ blockHash: string; txs: number; coinbaseTxid: string }> {
    const r = await this.call({ cmd: 'node.generate', payout_pk_hex: payoutPubHex });
    if (!r.ok) throw new Error(`node.generate failed: ${JSON.stringify(r)}`);
    return { blockHash: r.block_hash as string, txs: r.txs as number, coinbaseTxid: (r.coinbase_txid as string) ?? '' };
  }

  /** Submit a raw (signed) tx; the node validates it through its REAL Script interpreter. */
  async submitTx(rawTxHex: string): Promise<{ ok: boolean; reason: string; txid: string }> {
    const r = await this.call({ cmd: 'node.submit', raw_tx_hex: rawTxHex });
    return { ok: r.ok === true, reason: (r.reason as string) ?? '', txid: (r.txid as string) ?? '' };
  }

  /** Read-only UTXO status for an outpoint (REQ-NET-004 against the real node). */
  async outpointStatus(txidHex: string, vout: number): Promise<{ unspent: boolean; value: number }> {
    const r = await this.call({ cmd: 'node.outpoint', txid_hex: txidHex, vout });
    if (!r.ok) throw new Error(`node.outpoint failed: ${JSON.stringify(r)}`);
    return { unspent: r.unspent === true, value: (r.value as number) ?? 0 };
  }

  /** The current size of the node's UTXO set. */
  async utxoCount(): Promise<number> {
    const r = await this.call({ cmd: 'node.utxo_count' });
    if (!r.ok) throw new Error(`node.utxo_count failed: ${JSON.stringify(r)}`);
    return r.count as number;
  }

  async status(): Promise<NodeResponse> {
    return this.call({ cmd: 'status' });
  }

  async shutdown(): Promise<void> {
    try {
      await this.call({ cmd: 'shutdown' });
    } catch {
      /* daemon may close the socket on shutdown */
    }
  }
}
