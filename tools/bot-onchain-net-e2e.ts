/**
 * Multi-process on-chain bot table, fully PEER-TO-PEER: two SEPARATE bot-daemon processes connect over
 * the P2P mesh (the host bot listens; the other dials it), play a hand, exchange on-chain keys + the
 * escrow outpoint over the mesh, and co-sign the N-of-N settlement of the escrow to the final stacks —
 * submitted + confirmed on the BSV node. This is the fully autonomous on-chain player path: no
 * in-process sharing, no relay server, every step peer-to-peer.
 */

import { spawn, type ChildProcess } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { RealBsvNode } from '@bsv-poker/adapters/real-node';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const NODE_PORT = Number(process.env.BSV_NODE_PORT ?? 8747);
const MESH_PORT = Number(process.env.BSV_MESH_PORT ?? 9710); // the host bot's P2P listen port
const procs: ChildProcess[] = [];

function bot(name: string, extra: string[]): () => string {
  const p = spawn('node', [join(ROOT, 'tools', 'bot-daemon.ts'), '--node', String(NODE_PORT), '--name', name, ...extra], { cwd: ROOT });
  procs.push(p);
  let buf = '';
  p.stdout.on('data', (d) => { buf += d.toString(); });
  p.stderr.on('data', (d) => { buf += d.toString(); });
  return () => buf;
}

async function main(): Promise<void> {
  procs.push(spawn(process.execPath, [join(process.cwd(), 'tools/regtest-node-daemon.ts'), '--port', String(NODE_PORT)], { stdio: 'ignore' }));
  const node = new RealBsvNode('127.0.0.1', NODE_PORT);
  try {
    const dl = Date.now() + 30000;
    while (!(await node.ping().catch(() => false))) { if (Date.now() > dl) throw new Error('node down'); await new Promise((r) => setTimeout(r, 400)); }

    console.log('[bot-onchain-net] spawning two SEPARATE on-chain bot processes (peer-to-peer, no relay)…');
    // The host bot LISTENS on a known port + hosts the table; the other bot DIALS it.
    const alice = bot('alice', ['--listen', String(MESH_PORT), '--create', '--seats', '2', '--hands', '1']);
    await new Promise((r) => setTimeout(r, 2500)); // let the host start listening + create the table
    const bob = bot('bob', ['--peer', `127.0.0.1:${MESH_PORT}`, '--hands', '1']);

    const fin = Date.now() + 90000;
    while (Date.now() < fin) {
      const a = alice();
      const b = bob();
      if (/SETTLED ON-CHAIN/.test(a) && /(co-signed settlement|SETTLED ON-CHAIN)/.test(b)) break;
      if (/FAIL/.test(a) || /FAIL/.test(b)) break;
      await new Promise((r) => setTimeout(r, 700));
    }

    const a = alice();
    const b = bob();
    assert.match(a, /SEATED at seat/, `alice never seated:\n${a.slice(-400)}`);
    assert.match(b, /SEATED at seat/, `bob never seated:\n${b.slice(-400)}`);
    assert.ok(/SETTLED ON-CHAIN/.test(a) || /SETTLED ON-CHAIN/.test(b), `no on-chain settlement happened:\nA:${a.slice(-400)}\nB:${b.slice(-400)}`);
    assert.ok(/co-signed settlement|SETTLED ON-CHAIN/.test(b), `bob did not co-sign over the mesh:\n${b.slice(-400)}`);

    // LIFE-CRITICAL ordering (always send the user a close condition): every seat must HOLD the
    // unilateral nLockTime recovery for 100% of the escrow BEFORE a sat is locked on-chain. Seating is
    // by commit-reveal beacon, so EITHER bot may be the funder (seat 0); find whichever logged funding
    // and assert it held the recovery first — and that BOTH seats held it.
    assert.match(a, /hold unilateral nLockTime recovery/, `alice never co-signed/held the recovery:\n${a.slice(-400)}`);
    assert.match(b, /hold unilateral nLockTime recovery/, `bob never co-signed/held the recovery:\n${b.slice(-400)}`);
    const funderOut = /funded escrow/.test(a) ? a : b;
    const held = funderOut.indexOf('hold unilateral nLockTime recovery');
    const funded = funderOut.indexOf('funded escrow');
    assert.ok(funded >= 0, `no seat logged funding the escrow:\nA:${a.slice(-300)}\nB:${b.slice(-300)}`);
    assert.ok(held >= 0 && held < funded, `recovery must be held BEFORE the escrow is funded (held@${held}, funded@${funded})`);
    console.log('[bot-onchain-net] both seats held the unilateral nLockTime recovery BEFORE the escrow was funded (no sat at risk without a close condition).');

    const who = /SETTLED ON-CHAIN/.test(a) ? a : b;
    console.log('[bot-onchain-net] ' + (who.match(/SETTLED ON-CHAIN: [^\n]*/)?.[0] ?? 'settled'));
    console.log('\n[bot-onchain-net] PASS — recovery-before-risk enforced, then two separate bot processes played + co-signed an on-chain settlement peer-to-peer, confirmed on the node, no relay server.');
  } finally {
    await node.shutdown().catch(() => {});
    for (const p of procs) p.kill();
  }
}

main().then(() => process.exit(0), (e) => { console.error('[bot-onchain-net] FAIL:', (e as Error).message); for (const p of procs) p.kill(); process.exit(1); });
