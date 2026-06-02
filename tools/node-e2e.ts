/**
 * On-chain E2E against the REAL embedded BSV regtest node (core D6 / §10.2, REQ-DEP-004).
 * Starts the `bonded-subsat-channel` node daemon (the prof-faustus reference node), then drives
 * it from the platform's real node adapter: ping → height → mine blocks → height increments.
 * This proves the platform's chain backend binds to the real node (not a fake), on regtest.
 *
 * The node is run on the host (regtest only); the daemon is started here and stopped at the end
 * (never left as a zombie; never a reboot).
 *
 * Override the node repo location with BSV_NODE_DIR; default is the known checkout.
 */

import { spawn, type ChildProcess } from 'node:child_process';
import assert from 'node:assert/strict';
import { RealBsvNode } from '@bsv-poker/adapters/real-node';
import { genKeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex } from '@bsv-poker/protocol-types';

const NODE_DIR = process.env.BSV_NODE_DIR ?? 'D:\\claude\\ACM 01\\bonded-subsat-channel';
const PORT = Number(process.env.BSV_NODE_PORT ?? 8744);
let daemon: ChildProcess | null = null;

function startDaemon(): ChildProcess {
  // python -m channel.cli daemon-start --port PORT --db :memory:  (PYTHONPATH=src)
  const child = spawn(
    'python',
    ['-m', 'channel.cli', 'daemon-start', '--port', String(PORT), '--db', ':memory:'],
    { cwd: NODE_DIR, env: { ...process.env, PYTHONPATH: 'src' }, stdio: 'ignore' },
  );
  return child;
}

async function waitForNode(node: RealBsvNode, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      if (await node.ping()) return;
    } catch {
      /* not up yet */
    }
    if (Date.now() > deadline) throw new Error('real node did not come up');
    await new Promise((r) => setTimeout(r, 400));
  }
}

async function main(): Promise<void> {
  console.log(`[node-e2e] starting real bonded-subsat-channel node on :${PORT} (regtest)…`);
  daemon = startDaemon();
  const node = new RealBsvNode('127.0.0.1', PORT);
  try {
    await waitForNode(node, 30000);
    console.log('[node-e2e] node is up; ping OK.');

    const h0 = await node.height();
    console.log(`[node-e2e] initial height = ${h0}`);

    // The platform derives a payout key and mines two regtest blocks via the REAL node.
    const payout = bytesToHex(genKeyPair().pubCompressed);
    const b1 = await node.generateBlock(payout);
    const b2 = await node.generateBlock(payout);
    const h1 = await node.height();
    console.log(`[node-e2e] mined blocks ${b1.blockHash.slice(0, 16)}…, ${b2.blockHash.slice(0, 16)}…`);
    console.log(`[node-e2e] height after 2 blocks = ${h1}`);

    assert.equal(h1, h0 + 2, 'height advanced by exactly the two mined blocks');
    console.log('\n[node-e2e] PASS — the platform drove the REAL embedded BSV regtest node (D6).');
  } finally {
    await node.shutdown();
    if (daemon) daemon.kill();
  }
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('[node-e2e] FAIL:', (e as Error).message);
    if (daemon) daemon.kill();
    process.exit(1);
  },
);
