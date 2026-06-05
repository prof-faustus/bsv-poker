/**
 * On-chain E2E against the project's OWN in-tree BSV regtest node (core D6 / §10.2, REQ-DEP-004).
 * STANDALONE: starts the in-tree node daemon (`tools/regtest-node-daemon.ts`, a TCP server around
 * `@bsv-poker/adapters/regtest-node`) — no external process — then drives it through the node client:
 * ping → height → mine blocks → height increments. Proves the platform's chain backend binds to the
 * in-tree node (not a fake), on regtest.
 */

import { spawn, type ChildProcess } from 'node:child_process';
import { join } from 'node:path';
import assert from 'node:assert/strict';
import { RealBsvNode } from '@bsv-poker/adapters/real-node';
import { genKeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex } from '@bsv-poker/protocol-types';

const PORT = Number(process.env.BSV_NODE_PORT ?? 8744);
let daemon: ChildProcess | null = null;

function startDaemon(): ChildProcess {
  // The in-tree node daemon, spawned exactly the way relay-go/indexer-go are (the project's own code).
  return spawn(process.execPath, [join(process.cwd(), 'tools/regtest-node-daemon.ts'), '--port', String(PORT)], { stdio: 'ignore' });
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
  console.log(`[node-e2e] starting the in-tree regtest node on :${PORT} (standalone)…`);
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
    console.log('\n[node-e2e] PASS — the platform drove the project\'s OWN in-tree BSV regtest node (D6). Standalone — no external process.');
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
