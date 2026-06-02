/**
 * Waiting-room + real multiplayer E2E (app §A6.3/§A6.5/§A7) — proves two REAL players (not a bot)
 * find a table, join the waiting room, get seated by agreement, and play a full hand
 * interactively over the relay, converging byte-for-byte (REQ-TEST-002).
 *
 *   Host creates a 2-seat table → both join the waiting room → seats agreed → both run the
 *   interactive client (a scripted human acts on each turn) → identical final state.
 */

import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { join } from 'node:path';
import assert from 'node:assert/strict';
import type { Action, LegalActions } from '@bsv-poker/protocol-types';
import {
  RelayClient,
  LobbyClient,
  InteractiveNetworkedTableClient,
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

const passive = (legal: LegalActions, seat: number): Action => {
  if (legal.check) return { kind: 'check', seat, amount: 0 };
  if (legal.call) return { kind: 'call', seat, amount: legal.call.amount };
  return { kind: 'fold', seat, amount: 0 };
};

const META: TableMeta = {
  name: 'Friday night HU',
  variant: 'holdem',
  smallBlind: 1,
  bigBlind: 2,
  startingStack: 100,
  maxSeats: 2,
};

async function player(
  relayBase: string,
  me: { id: string; pub: string },
  tableId: string,
  entropySeed: number,
): Promise<{ stateHash: string }> {
  const lobby = new LobbyClient(new RelayClient(relayBase));
  const { seated } = lobby.joinWaitingRoom(tableId, me, META, (players) =>
    console.log(`[${me.id}] waiting room now has ${players.length} player(s): ${players.map((p) => p.id).join(', ')}`),
  );
  const seat = await seated;
  console.log(`[${me.id}] seated at seat ${seat.mySeat} of ${seat.seats.length}`);

  const client = new InteractiveNetworkedTableClient({
    relay: new RelayClient(relayBase),
    tableId,
    mySeat: seat.mySeat,
    seats: seat.seats,
    ruleset: seat.ruleset,
    entropy: Uint8Array.from(Array.from({ length: 32 }, (_, i) => (i * entropySeed + 3) % 251)),
  });
  // The "human": act on every turn via the UI-facing update stream.
  client.onUpdate((u: ClientUpdate) => {
    if (u.yourTurn && u.legal) client.submitAction(passive(u.legal, u.mySeat));
  });
  await client.play();
  return { stateHash: client.stateHash()! };
}

async function main(): Promise<void> {
  console.log('[lobby-e2e] starting relay (:8091) + indexer (:8092)…');
  startService('apps/relay-go', '127.0.0.1:8091', 'relay-go');
  startService('apps/indexer-go', '127.0.0.1:8092', 'indexer-go');
  await waitHealthy('http://127.0.0.1:8091/healthz', 30000);

  const base = 'http://127.0.0.1:8091';
  const host = new LobbyClient(new RelayClient(base));
  const tableId = await host.createTable(META);
  console.log(`[lobby-e2e] host created table ${tableId} (${META.name}); now visible in the lobby:`);
  for (const t of await host.listTables()) console.log(`   - ${t.id}: ${t.meta.name} (${t.meta.maxSeats} seats)`);

  // Two real players discover + join the waiting room and play.
  const [a, b] = await Promise.all([
    player(base, { id: 'alice', pub: '02aa' }, tableId, 7),
    player(base, { id: 'bob', pub: '03bb' }, tableId, 19),
  ]);

  console.log(`[lobby-e2e] alice final stateHash: ${a.stateHash.slice(0, 24)}…`);
  console.log(`[lobby-e2e] bob   final stateHash: ${b.stateHash.slice(0, 24)}…`);
  assert.equal(a.stateHash, b.stateHash, 'both players converged on identical state');
  console.log('\n[lobby-e2e] PASS — two players joined a waiting room and played a real hand (no bot).');
}

main().then(
  () => {
    cleanup();
    process.exit(0);
  },
  (e) => {
    console.error('[lobby-e2e] FAIL:', (e as Error).message);
    cleanup();
    process.exit(1);
  },
);
