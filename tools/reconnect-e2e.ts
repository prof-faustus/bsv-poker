/**
 * Reconnect / resume E2E (core §8.6/§12.3, REQ-NET-007, REQ-DATA-002/003). Two players play a
 * hand, dual-pathing each move to the indexer (canonical). A SEPARATE observer then fetches the
 * table transcript from the indexer and rebuilds the hand's final state — and it matches the
 * players' state byte-for-byte. This is exactly what a reconnecting client does to resume.
 */

import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { join } from 'node:path';
import assert from 'node:assert/strict';
import type { Action, LegalActions } from '@bsv-poker/protocol-types';
import {
  RelayClient,
  IndexerClient,
  LobbyClient,
  InteractiveNetworkedTableClient,
  rebuildHand,
  rulesetFromMeta,
  type TableMeta,
  type ClientUpdate,
} from '@bsv-poker/app-services';

const ROOT = process.cwd();
const isWin = process.platform === 'win32';
const children: ChildProcess[] = [];
const RELAY = 'http://127.0.0.1:8091';
const INDEXER = 'http://127.0.0.1:8092';
const META: TableMeta = { name: 'reconnect', variant: 'holdem', smallBlind: 1, bigBlind: 2, startingStack: 100, maxSeats: 2 };

function startService(dir: string, addr: string, bin: string): void {
  const exe = isWin ? `${bin}.exe` : bin;
  const b = spawnSync('go', ['build', '-o', exe, '.'], { cwd: join(ROOT, dir), stdio: 'inherit' });
  if (b.status !== 0) throw new Error(`go build -o failed in ${dir}`);
  children.push(spawn(join(ROOT, dir, exe), ['-addr', addr], { stdio: 'ignore' }));
}
async function waitHealthy(url: string, ms: number): Promise<void> {
  const dl = Date.now() + ms;
  for (;;) {
    try {
      if ((await fetch(url, { signal: AbortSignal.timeout(1000) })).ok) return;
    } catch {
      /* not up */
    }
    if (Date.now() > dl) throw new Error(`not healthy: ${url}`);
    await new Promise((r) => setTimeout(r, 400));
  }
}
const passive = (l: LegalActions, seat: number): Action =>
  l.check ? { kind: 'check', seat, amount: 0 } : l.call ? { kind: 'call', seat, amount: l.call.amount } : { kind: 'fold', seat, amount: 0 };

async function player(tableId: string, id: string): Promise<string> {
  const lobby = new LobbyClient(new RelayClient(RELAY));
  const { seated } = lobby.joinWaitingRoom(tableId, { id, pub: randomBytes(33).toString('hex') }, META, undefined, { allowUnsigned: true });
  const seat = await seated;
  const client = new InteractiveNetworkedTableClient({
    relay: new RelayClient(RELAY),
    indexer: new IndexerClient(INDEXER), // dual-path each move to the canonical store
    tableId,
    mySeat: seat.mySeat,
    seats: seat.seats,
    ruleset: seat.ruleset,
    entropy: randomBytes(32),
    allowUnsigned: true, // test fixture (audit 1)
  });
  client.onUpdate((u: ClientUpdate) => {
    if (u.yourTurn && u.legal) client.submitAction(passive(u.legal, u.mySeat));
  });
  await client.play();
  return client.stateHash()!;
}

async function main(): Promise<void> {
  console.log('[reconnect-e2e] starting relay + indexer…');
  startService('apps/relay-go', '127.0.0.1:8091', 'relay-go');
  startService('apps/indexer-go', '127.0.0.1:8092', 'indexer-go');
  await waitHealthy(`${RELAY}/healthz`, 30000);
  await waitHealthy(`${INDEXER}/healthz`, 30000);

  const host = new LobbyClient(new RelayClient(RELAY));
  const tableId = await host.createTable(META);
  const [a, b] = await Promise.all([player(tableId, 'alice'), player(tableId, 'bob')]);
  assert.equal(a, b, 'players agree on the hand');
  console.log(`[reconnect-e2e] players' final stateHash: ${a.slice(0, 24)}…`);

  // A reconnecting observer: fetch the transcript from the indexer and rebuild.
  const indexer = new IndexerClient(INDEXER);
  const records = await indexer.records(tableId);
  console.log(`[reconnect-e2e] fetched ${records.length} transcript records from the indexer`);
  const seats = [
    { seat: 0, stack: META.startingStack },
    { seat: 1, stack: META.startingStack },
  ];
  const byClass: Record<string, number> = {};
  for (const r of records) byClass[r.class] = (byClass[r.class] ?? 0) + 1;
  console.log(`[reconnect-e2e] record classes: ${JSON.stringify(byClass)}`);
  const rebuilt = rebuildHand(records, rulesetFromMeta(META), seats, 0, 0);
  console.log(`[reconnect-e2e] rebuilt: complete=${rebuilt.state.handComplete} board=${rebuilt.state.board.length} stacks=${rebuilt.state.seats.map((s) => s.stack).join(',')}`);
  console.log(`[reconnect-e2e] rebuilt-from-transcript stateHash: ${rebuilt.stateHash.slice(0, 24)}…`);
  assert.equal(rebuilt.stateHash, a, 'reconnect: rebuilt-from-transcript state matches the live players');
  assert.equal(rebuilt.state.handComplete, true);
  console.log('\n[reconnect-e2e] PASS — rejoined from the transcript and rebuilt identical state (REQ-NET-007).');
}

main().then(
  () => {
    for (const c of children) c.kill();
    process.exit(0);
  },
  (e) => {
    console.error('[reconnect-e2e] FAIL:', (e as Error).message);
    for (const c of children) c.kill();
    process.exit(1);
  },
);
