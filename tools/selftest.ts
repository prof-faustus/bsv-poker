/**
 * Self-contained stack self-test (core §10.2/§10.3, REQ-VM-001) — the VM bootstrap's "bring
 * the stack up, run self-tests, print a transcript" step. Runnable WITHOUT Docker so the gate
 * is checkable here:
 *   1. build the Go services (proves the stack compiles);
 *   2. start relay (:8091) + indexer (:8092); poll /healthz until ready;
 *   3. run a full heads-up Hold'em hand in-process (the client/engine role) and print the
 *      transcript (ordered actions + final state hash + payouts);
 *   4. tear the services down.
 *
 * Phase-0 note: the BSV regtest node (D6) is the in-tree node (adapters/regtest-node) bound by the adapter in
 * a later step; here the node/chain role is represented by the indexer projection + BS fake.
 */

import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { join } from 'node:path';
import { parseHand, type Card, type Ruleset, type Action } from '@bsv-poker/protocol-types';
import { createHoldem } from '@bsv-poker/game-holdem';
import { RelayClient, IndexerClient } from '@bsv-poker/app-services';
import assert from 'node:assert/strict';

const ROOT = process.cwd();
const children: ChildProcess[] = [];

const isWin = process.platform === 'win32';

/** Build a standalone binary and run IT directly, so killing the child stops the server (no
 *  orphaned `go run` server process / zombie — important per the host's no-zombie discipline). */
function startService(dir: string, addr: string, bin: string): ChildProcess {
  const exe = isWin ? `${bin}.exe` : bin;
  const b = spawnSync('go', ['build', '-o', exe, '.'], { cwd: join(ROOT, dir), stdio: 'inherit' });
  if (b.status !== 0) throw new Error(`go build -o failed in ${dir}`);
  const child = spawn(join(ROOT, dir, exe), ['-addr', addr], { stdio: 'ignore' });
  children.push(child);
  return child;
}

async function waitHealthy(url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    try {
      const res = await fetch(url, { signal: AbortSignal.timeout(1000) });
      if (res.ok) {
        const body = await res.text();
        if (body.includes('ok')) return;
      }
    } catch {
      /* not up yet */
    }
    if (Date.now() > deadline) throw new Error(`service not healthy: ${url}`);
    await new Promise((r) => setTimeout(r, 400));
  }
}

function fixedDeck(): Card[] {
  const head = ['As', 'Ks', 'Ah', 'Kh', 'Qd', 'Jc', '9h', '4s', '3h'].map((c) => parseHand(c)[0]!);
  const used = new Set(head);
  const rest: Card[] = [];
  for (let c = 0; c < 52; c++) if (!used.has(c)) rest.push(c);
  return [...head, ...rest];
}

function runHand(): { transcript: Action[]; stateHash: string; payouts: unknown } {
  const ruleset: Ruleset = {
    variant: 'holdem',
    bettingStructure: 'NL',
    forcedBetModel: 'blinds',
    seats: 2,
    blinds: { smallBlind: 1, bigBlind: 2, ante: 0, bringIn: 0 },
    minBuyIn: 100,
    maxBuyIn: 200,
    timeouts: { decisionMs: 30000, recoveryMs: 120000 },
    signingMode: 'A',
    currency: 'play-regtest',
    suitTiebreakHouseRule: false,
    hiLo: false,
  };
  const m = createHoldem({ deck: fixedDeck() });
  let s = m.init(ruleset, [
    { seat: 0, stack: 100 },
    { seat: 1, stack: 100 },
  ]);
  const transcript: Action[] = [
    { kind: 'call', seat: 0, amount: 1 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 0, amount: 0 },
    { kind: 'check', seat: 1, amount: 0 },
    { kind: 'check', seat: 0, amount: 0 },
  ];
  for (const a of transcript) s = m.apply(s, a);
  if (!s.handComplete) throw new Error('hand did not complete');
  return { transcript, stateHash: m.stateHash(s), payouts: s.payouts };
}

/** Exercise the real relay + indexer over HTTP: discovery, dual-path, deterministic projection. */
async function exerciseNetwork(): Promise<void> {
  const relay = new RelayClient('http://127.0.0.1:8091');
  const indexer = new IndexerClient('http://127.0.0.1:8092');

  // Tier-A discovery: two players announce presence and find each other.
  await relay.heartbeat('alice', '127.0.0.1:6001');
  await relay.heartbeat('bob', '127.0.0.1:6002');
  const presence = await relay.listPresence();
  assert.ok(presence.length >= 2, 'both players present');

  // Create a table; both will publish/ingest to it. Unique id per run (no cross-run collision).
  const tableId = `selftest-table-${Date.now()}`;
  await relay.createTable(tableId, 'Self-test HU');
  assert.ok((await relay.listTables()).some((t) => t.id === tableId), 'table discoverable');

  // Dual-path (REQ-NET-003): speed path = relay publish; canonical path = indexer ingest.
  await relay.publish(tableId, new TextEncoder().encode('action:bet:6')); // no subscribers ⇒ 0 delivered, fine
  const recs = [
    { txid: 'tx-funding', class: 'Funding', tableId },
    { txid: 'tx-deal', class: 'Deal', tableId },
    { txid: 'tx-bet1', class: 'Action', tableId },
  ];
  for (const r of recs) assert.equal(await indexer.ingest(r), true, `ingest ${r.txid}`);
  // dedup: re-ingest returns false
  assert.equal(await indexer.ingest(recs[0]!), false, 'dedup by txid');

  // The projection is the ordered valid-tx set, reconstructible identically by any client (P2).
  const projection = await indexer.table(tableId);
  assert.deepEqual(projection, ['tx-funding', 'tx-deal', 'tx-bet1'], 'deterministic ordered projection');
  console.log(`[selftest] indexer projection for ${tableId}: [${projection.join(', ')}]`);
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

async function main(): Promise<void> {
  try {
    console.log('[selftest] building + starting Go services (relay :8091, indexer :8092)…');
    startService('apps/relay-go', '127.0.0.1:8091', 'relay-go');
    startService('apps/indexer-go', '127.0.0.1:8092', 'indexer-go');
    await waitHealthy('http://127.0.0.1:8091/healthz', 30000);
    await waitHealthy('http://127.0.0.1:8092/healthz', 30000);
    console.log('[selftest] relay + indexer healthy.');

    console.log('[selftest] exercising relay + indexer over HTTP (discovery + dual-path)…');
    await exerciseNetwork();

    console.log('[selftest] running a full heads-up Hold\'em hand (client/engine role)…');
    const { transcript, stateHash, payouts } = runHand();
    console.log('[selftest] TRANSCRIPT:');
    transcript.forEach((a, i) => console.log(`   ${i}: seat ${a.seat} ${a.kind}${a.amount ? ' ' + a.amount : ''}`));
    console.log(`[selftest] final state hash: ${stateHash}`);
    console.log(`[selftest] payouts: ${JSON.stringify(payouts)}`);

    console.log('\n[selftest] PASS — VM stack came up end-to-end and a full hand settled.');
  } finally {
    cleanup();
  }
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('[selftest] FAIL:', (e as Error).message);
    cleanup();
    process.exit(1);
  },
);
