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

export interface NodeResponse {
  ok: boolean;
  [k: string]: unknown;
}

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
        const nl = buf.indexOf('\n');
        if (nl >= 0) {
          try {
            done(null, JSON.parse(buf.slice(0, nl)) as NodeResponse);
          } catch (e) {
            done(e as Error);
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

  /** Mine a regtest block paying out to `payoutPubHex` (compressed secp256k1 pubkey hex). */
  async generateBlock(payoutPubHex: string): Promise<{ blockHash: string; txs: number }> {
    const r = await this.call({ cmd: 'node.generate', payout_pk_hex: payoutPubHex });
    if (!r.ok) throw new Error(`node.generate failed: ${JSON.stringify(r)}`);
    return { blockHash: r.block_hash as string, txs: r.txs as number };
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
