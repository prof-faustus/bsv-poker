/**
 * Bot-over-the-wire E2E: proves bots are SEPARATE PROCESSES that connect like remote players. We
 * start a relay, create a table, then spawn TWO independent bot-daemon processes that join the same
 * table over the relay socket and play hands. The test asserts both daemons seat, act over the wire,
 * and the session runs — exactly as if two remote humans (each in their own window) were playing. No
 * bot is in the main app's process.
 */

import { spawn, type ChildProcess } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { RelayClient, LobbyClient, type TableMeta } from '@bsv-poker/app-services';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const RELAY_BIN = join(ROOT, 'apps', 'relay-go', 'relay-go.exe');
const PORT = Number(process.env.BOT_E2E_PORT ?? 8097);
const RELAY_URL = `http://127.0.0.1:${PORT}`;

const procs: ChildProcess[] = [];
function spawnBot(table: string, name: string): { proc: ChildProcess; out: () => string } {
  const p = spawn('node', [join(ROOT, 'tools', 'bot-daemon.ts'), '--relay', RELAY_URL, '--table', table, '--name', name, '--hands', '2'], { cwd: ROOT });
  procs.push(p);
  let buf = '';
  p.stdout.on('data', (d) => { buf += d.toString(); });
  p.stderr.on('data', (d) => { buf += d.toString(); });
  return { proc: p, out: () => buf };
}

async function main(): Promise<void> {
  const relay = spawn(RELAY_BIN, ['-addr', `127.0.0.1:${PORT}`], { stdio: 'ignore' });
  procs.push(relay);
  try {
    const client = new RelayClient(RELAY_URL);
    const dl = Date.now() + 15000;
    while (!(await client.health().catch(() => false))) {
      if (Date.now() > dl) throw new Error('relay did not start');
      await new Promise((r) => setTimeout(r, 300));
    }

    const meta: TableMeta = { name: 'bot-test', variant: 'holdem', smallBlind: 1, bigBlind: 2, startingStack: 100, maxSeats: 2 };
    const tableId = await new LobbyClient(client).createTable(meta);
    console.log(`[bot-net] created table ${tableId}; spawning two SEPARATE bot processes…`);

    const alice = spawnBot(tableId, 'alice');
    const bob = spawnBot(tableId, 'bob');

    // Wait for the session to play out across the two processes.
    const finished = Date.now() + 40000;
    while (Date.now() < finished) {
      const a = alice.out();
      const b = bob.out();
      const bothSeated = /SEATED at seat/.test(a) && /SEATED at seat/.test(b);
      const bothActed = /my turn →/.test(a) && /my turn →/.test(b);
      const done = /session ended/.test(a) || /session ended/.test(b);
      if (bothSeated && bothActed && done) break;
      await new Promise((r) => setTimeout(r, 500));
    }

    const a = alice.out();
    const b = bob.out();
    assert.match(a, /SEATED at seat/, `alice never seated:\n${a}`);
    assert.match(b, /SEATED at seat/, `bob never seated:\n${b}`);
    assert.match(a, /my turn →/, `alice never acted over the wire:\n${a}`);
    assert.match(b, /my turn →/, `bob never acted over the wire:\n${b}`);
    console.log('[bot-net] alice + bob (separate processes) seated and played over the relay socket.');
    console.log('\n[bot-net] PASS — bots are remote players over the socket, not in the app process.');
  } finally {
    for (const p of procs) p.kill();
  }
}

main().then(() => process.exit(0), (e) => { console.error('[bot-net] FAIL:', (e as Error).message); for (const p of procs) p.kill(); process.exit(1); });
