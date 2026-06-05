/**
 * Variant-generic, multi-seat multiplayer E2E (v3). Proves real players can choose a variant and
 * sit N-handed: runs a 3-handed Texas Hold'em and a 2-handed Omaha over the relay through the
 * lobby + interactive client, asserting all players converge byte-for-byte (REQ-TEST-002).
 *
 * (Hold'em/Omaha use the check/bet/call/raise/fold action set, so a passive auto-strategy drives
 * them headlessly. Stud/Razz/Draw add bring-in/draw actions that a human supplies in the UI; their
 * engines are covered by the module unit tests — this harness proves the networked generic path.)
 */

import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { join } from 'node:path';
import assert from 'node:assert/strict';
import type { Variant } from '@bsv-poker/protocol-types';
import {
  RelayClient,
  LobbyClient,
  InteractiveNetworkedTableClient,
  universalBot,
  type TableMeta,
  type ClientUpdate,
} from '@bsv-poker/app-services';

const ROOT = process.cwd();
const isWin = process.platform === 'win32';
const children: ChildProcess[] = [];

function startService(dir: string, addr: string, bin: string): void {
  const exe = isWin ? `${bin}.exe` : bin;
  const b = spawnSync('go', ['build', '-o', exe, '.'], { cwd: join(ROOT, dir), stdio: 'inherit' });
  if (b.status !== 0) throw new Error(`go build -o failed in ${dir}`);
  children.push(spawn(join(ROOT, dir, exe), ['-addr', addr], { stdio: 'ignore' }));
}
async function waitHealthy(url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      if ((await fetch(url, { signal: AbortSignal.timeout(1000) })).ok) return;
    } catch {
      /* not up */
    }
    if (Date.now() > deadline) throw new Error(`not healthy: ${url}`);
    await new Promise((r) => setTimeout(r, 400));
  }
}
function cleanup(): void {
  for (const c of children) {
    try {
      c.kill();
    } catch {
      /* ignore */
    }
  }
}

async function joinAndPlay(base: string, tableId: string, meta: TableMeta, id: string): Promise<string> {
  const lobby = new LobbyClient(new RelayClient(base));
  const pub = randomBytes(33).toString('hex');
  const { seated } = lobby.joinWaitingRoom(tableId, { id, pub }, meta, undefined, { allowUnsigned: true });
  const seat = await seated;
  const client = new InteractiveNetworkedTableClient({
    relay: new RelayClient(base),
    tableId,
    mySeat: seat.mySeat,
    seats: seat.seats,
    ruleset: seat.ruleset,
    entropy: randomBytes(32),
    allowUnsigned: true, // test fixture (audit 1)
  });
  client.onUpdate((u: ClientUpdate) => {
    if (u.yourTurn && u.legal) client.submitAction(universalBot(u.legal, u.mySeat));
  });
  await client.play();
  return client.stateHash()!;
}

async function scenario(base: string, variant: Variant, seats: number): Promise<void> {
  const meta: TableMeta = {
    name: `${variant} ${seats}-handed`,
    variant,
    smallBlind: 1,
    bigBlind: 2,
    startingStack: 100,
    maxSeats: seats,
  };
  const host = new LobbyClient(new RelayClient(base));
  const tableId = await host.createTable(meta);
  console.log(`\n[multi-e2e] ${variant} ${seats}-handed → table ${tableId}`);
  const ids = Array.from({ length: seats }, (_, i) => `p${i}`);
  const hashes = await Promise.all(ids.map((id) => joinAndPlay(base, tableId, meta, id)));
  for (let i = 1; i < hashes.length; i++) {
    assert.equal(hashes[i], hashes[0], `${variant}: seat ${i} diverged`);
  }
  console.log(`[multi-e2e] ${variant} ${seats}-handed: all ${seats} players converged → ${hashes[0]!.slice(0, 20)}…`);
}

async function main(): Promise<void> {
  console.log('[multi-e2e] starting relay (:8091) + indexer (:8092)…');
  startService('apps/relay-go', '127.0.0.1:8091', 'relay-go');
  startService('apps/indexer-go', '127.0.0.1:8092', 'indexer-go');
  await waitHealthy('http://127.0.0.1:8091/healthz', 30000);

  const base = 'http://127.0.0.1:8091';
  await scenario(base, 'holdem', 3); // multi-seat (3-handed)
  await scenario(base, 'omaha', 2); // 4 hole cards, 2+3
  await scenario(base, 'stud', 2); // ante + bring-in, up/down cards
  await scenario(base, 'draw', 2); // discard + redraw
  await scenario(base, 'razz', 2); // ace-to-five low

  console.log('\n[multi-e2e] PASS — ALL FIVE variants play multiplayer over the relay (incl. 3-handed).');
}

main().then(
  () => {
    cleanup();
    process.exit(0);
  },
  (e) => {
    console.error('[multi-e2e] FAIL:', (e as Error).message);
    cleanup();
    process.exit(1);
  },
);
