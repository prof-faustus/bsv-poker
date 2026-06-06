/**
 * Browser transport over the player's OWN local node — SERVERLESS. The browser cannot be a raw TCP
 * peer, so each player runs their own local node (tools/local-node.ts) and the browser talks only to
 * localhost. This e2e proves the EXACT browser path works peer-to-peer with no central server:
 *
 *   browser RelayClient (HTTP/SSE) → player A's own local node → P2P mesh → player B's own local node
 *   → browser RelayClient (HTTP/SSE)
 *
 * Two `RelayClient`s — the SAME class the web client uses — each point at a DIFFERENT local node
 * (different loopback port = different player's machine). The two local nodes are peers on the mesh.
 * A full hand played through the lobby + interactive client must converge byte-for-byte, proving a
 * browser-bound player finds and plays a game with only their own local node and no server.
 */
import { spawn, type ChildProcess } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import type { Action, LegalActions } from '@bsv-poker/protocol-types';
import {
  RelayClient,
  LobbyClient,
  InteractiveNetworkedTableClient,
  type TableMeta,
  type ClientUpdate,
} from '@bsv-poker/app-services';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const HTTP_A = Number(process.env.BSV_LN_A_HTTP ?? 8190);
const HTTP_B = Number(process.env.BSV_LN_B_HTTP ?? 8191);
const MESH_A = Number(process.env.BSV_LN_A_MESH ?? 9720);
const META: TableMeta = { name: 'Browser HU (own local node)', variant: 'holdem', smallBlind: 1, bigBlind: 2, startingStack: 100, maxSeats: 2 };
const procs: ChildProcess[] = [];

const passive = (l: LegalActions, seat: number): Action =>
  l.check ? { kind: 'check', seat, amount: 0 } : l.call ? { kind: 'call', seat, amount: l.call.amount } : { kind: 'fold', seat, amount: 0 };

function spawnLocalNode(args: string[]): void {
  procs.push(spawn('node', [join(ROOT, 'tools', 'local-node.ts'), ...args], { cwd: ROOT, stdio: 'ignore' }));
}
async function waitHealthy(base: string, ms: number): Promise<void> {
  const dl = Date.now() + ms;
  for (;;) {
    try { if ((await fetch(`${base}/healthz`, { signal: AbortSignal.timeout(1000) })).ok) return; } catch { /* not up */ }
    if (Date.now() > dl) throw new Error(`local node not healthy: ${base}`);
    await new Promise((r) => setTimeout(r, 200));
  }
}

/** A browser-bound player: its transport is a RelayClient (HTTP/SSE) to its OWN local node. */
async function player(base: string, id: string, pub: string, tableId: string, seed: number): Promise<string> {
  const lobby = new LobbyClient(new RelayClient(base));
  const { seated } = lobby.joinWaitingRoom(tableId, { id, pub }, META, undefined, { allowUnsigned: true });
  const seat = await seated;
  const client = new InteractiveNetworkedTableClient({
    relay: new RelayClient(base), // the browser's own local node (loopback) — no central server
    tableId,
    mySeat: seat.mySeat,
    seats: seat.seats,
    ruleset: seat.ruleset,
    entropy: Uint8Array.from(Array.from({ length: 32 }, (_, i) => (i * seed + 3) % 251)),
    allowUnsigned: true, // test fixture (audit 1)
  });
  client.onUpdate((u: ClientUpdate) => { if (u.yourTurn && u.legal) client.submitAction(passive(u.legal, u.mySeat)); });
  await client.play();
  return client.stateHash()!;
}

async function main(): Promise<void> {
  const baseA = `http://127.0.0.1:${HTTP_A}`;
  const baseB = `http://127.0.0.1:${HTTP_B}`;
  console.log('[browser-transport] starting two LOCAL NODES (one per player), peers on the mesh — no central server…');
  spawnLocalNode(['--http', String(HTTP_A), '--listen', String(MESH_A)]); // player A's node (hosts the mesh)
  spawnLocalNode(['--http', String(HTTP_B), '--listen', '0', '--peer', `127.0.0.1:${MESH_A}`]); // player B dials A
  try {
    await Promise.all([waitHealthy(baseA, 20000), waitHealthy(baseB, 20000)]);

    // Player A (a browser) hosts a table via its OWN local node; the announce gossips to B's node.
    const host = new LobbyClient(new RelayClient(baseA));
    const tableId = await host.createTable(META);
    console.log(`[browser-transport] player A's browser hosted table ${tableId}; discovering at player B's node…`);
    const lobbyB = new LobbyClient(new RelayClient(baseB));
    const dl = Date.now() + 10000;
    while (!(await lobbyB.listTables()).some((t) => t.id === tableId)) {
      if (Date.now() > dl) throw new Error('table did not gossip across the two local nodes');
      await new Promise((r) => setTimeout(r, 150));
    }
    console.log('[browser-transport] player B discovered the table through its OWN local node (gossiped across the mesh).');

    const [a, b] = await Promise.all([
      player(baseA, 'alice', '02aa', tableId, 7),
      player(baseB, 'bob', '03bb', tableId, 19),
    ]);
    assert.equal(a, b, 'the two browser-bound players converged on identical state');
    console.log(`[browser-transport] both browsers converged: ${a.slice(0, 24)}…`);
    console.log('\n[browser-transport] PASS — a browser-bound player finds AND plays a game through ONLY its own local node (HTTP/SSE → P2P mesh); two players converged with no central server.');
  } finally {
    for (const p of procs) p.kill();
  }
}

main().then(() => process.exit(0), (e) => { console.error('[browser-transport] FAIL:', (e as Error).message); for (const p of procs) p.kill(); process.exit(1); });
