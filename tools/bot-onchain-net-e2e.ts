/**
 * Multi-process on-chain bot table: two SEPARATE bot-daemon processes connect over the relay, play a
 * hand, exchange on-chain keys + the escrow outpoint over the relay, and co-sign the N-of-N
 * settlement of the escrow to the final stacks — submitted + confirmed on the real node. This is the
 * fully autonomous on-chain player path: no in-process sharing, every step over the wire.
 */

import { spawn, type ChildProcess } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { RealBsvNode } from '@bsv-poker/adapters/real-node';
import { RelayClient } from '@bsv-poker/app-services';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const NODE_PORT = Number(process.env.BSV_NODE_PORT ?? 8747);
const RELAY_PORT = Number(process.env.BSV_RELAY_PORT ?? 8101);
const RELAY_URL = `http://127.0.0.1:${RELAY_PORT}`;
const procs: ChildProcess[] = [];

function bot(name: string, extra: string[]): () => string {
  const p = spawn('node', [join(ROOT, 'tools', 'bot-daemon.ts'), '--relay', RELAY_URL, '--node', String(NODE_PORT), '--name', name, ...extra], { cwd: ROOT });
  procs.push(p);
  let buf = '';
  p.stdout.on('data', (d) => { buf += d.toString(); });
  p.stderr.on('data', (d) => { buf += d.toString(); });
  return () => buf;
}

async function main(): Promise<void> {
  procs.push(spawn(process.execPath, [join(process.cwd(), 'tools/regtest-node-daemon.ts'), '--port', String(NODE_PORT)], { stdio: 'ignore' }));
  procs.push(spawn(join(ROOT, 'apps', 'relay-go', 'relay-go.exe'), ['-addr', `127.0.0.1:${RELAY_PORT}`], { stdio: 'ignore' }));
  const node = new RealBsvNode('127.0.0.1', NODE_PORT);
  try {
    const dl = Date.now() + 30000;
    while (!(await node.ping().catch(() => false))) { if (Date.now() > dl) throw new Error('node down'); await new Promise((r) => setTimeout(r, 400)); }
    while (!(await new RelayClient(RELAY_URL).health().catch(() => false))) { if (Date.now() > dl) throw new Error('relay down'); await new Promise((r) => setTimeout(r, 300)); }

    console.log('[bot-onchain-net] spawning two SEPARATE on-chain bot processes…');
    const alice = bot('alice', ['--create', '--seats', '2', '--hands', '1']);
    await new Promise((r) => setTimeout(r, 2500)); // let the host create the table + start coordinating
    const bob = bot('bob', ['--hands', '1']);

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
    assert.ok(/co-signed settlement|SETTLED ON-CHAIN/.test(b), `bob did not co-sign over the relay:\n${b.slice(-400)}`);
    const who = /SETTLED ON-CHAIN/.test(a) ? a : b;
    console.log('[bot-onchain-net] ' + (who.match(/SETTLED ON-CHAIN: [^\n]*/)?.[0] ?? 'settled'));
    console.log('\n[bot-onchain-net] PASS — two separate bot processes played + co-signed an on-chain settlement over the relay, confirmed on the node.');
  } finally {
    await node.shutdown().catch(() => {});
    for (const p of procs) p.kill();
  }
}

main().then(() => process.exit(0), (e) => { console.error('[bot-onchain-net] FAIL:', (e as Error).message); for (const p of procs) p.kill(); process.exit(1); });
