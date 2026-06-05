/**
 * Validating-indexer E2E (audit finding 7) — proves, against the REAL Go indexer in VALIDATING mode
 * (`-validate`), that:
 *   1. two signed interactive players play a full hand;
 *   2. their seat→pubkey map is registered and EVERY signed envelope is authenticated and accepted
 *      by the indexer (the transcript is non-empty and rebuilds to the same state — so a real client
 *      signature really does verify against the Go validator, byte-for-byte);
 *   3. a FORGED record posted directly to /ingest is REJECTED (400) — the boundary is fail-closed.
 *
 * This is the live counterpart to the hermetic Go tests (indexer/validate_test.go, INV-IXV-*).
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
  validateHandLegality,
  rulesetFromMeta,
  sessionAuthFromSeed,
  deriveSeatSeed,
  type TableMeta,
  type ClientUpdate,
} from '@bsv-poker/app-services';

const ROOT = process.cwd();
const isWin = process.platform === 'win32';
const children: ChildProcess[] = [];
const RELAY = 'http://127.0.0.1:8091';
const INDEXER = 'http://127.0.0.1:8092';
const META: TableMeta = { name: 'validating', variant: 'holdem', smallBlind: 1, bigBlind: 2, startingStack: 100, maxSeats: 2 };

/** Start a Go service binary; `extraArgs` lets us pass -validate to the indexer. */
function startService(dir: string, addr: string, bin: string, extraArgs: string[] = []): void {
  const exe = isWin ? `${bin}.exe` : bin;
  const b = spawnSync('go', ['build', '-o', exe, '.'], { cwd: join(ROOT, dir), stdio: 'inherit' });
  if (b.status !== 0) throw new Error(`go build -o failed in ${dir}`);
  children.push(spawn(join(ROOT, dir, exe), ['-addr', addr, ...extraArgs], { stdio: 'ignore' }));
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

/** A signed player: real Ed25519 session key, signed lobby join, signed envelopes, dual-path to the indexer. */
async function player(tableId: string, id: string): Promise<string> {
  const root = randomBytes(32);
  const auth = await sessionAuthFromSeed(deriveSeatSeed(root, 'bsv-poker/seat-ed25519'));
  const lobby = new LobbyClient(new RelayClient(RELAY));
  const { seated } = lobby.joinWaitingRoom(tableId, { id, pub: auth.pub, sign: (m) => auth.sign(m) }, META);
  const seat = await seated;
  const client = new InteractiveNetworkedTableClient({
    relay: new RelayClient(RELAY),
    indexer: new IndexerClient(INDEXER), // dual-path each signed move to the VALIDATING indexer
    tableId,
    mySeat: seat.mySeat,
    seats: seat.seats,
    ruleset: seat.ruleset,
    entropy: randomBytes(32),
    auth, // sign every envelope
    seatPubs: seat.players.map((p) => p.pub), // seat → registered session key
  });
  client.onUpdate((u: ClientUpdate) => {
    if (u.yourTurn && u.legal) client.submitAction(passive(u.legal, u.mySeat));
  });
  await client.play();
  return client.stateHash()!;
}

async function main(): Promise<void> {
  console.log('[validating-indexer-e2e] starting relay + indexer (-validate)…');
  startService('apps/relay-go', '127.0.0.1:8091', 'relay-go');
  startService('apps/indexer-go', '127.0.0.1:8092', 'indexer-go', ['-validate']);
  await waitHealthy(`${RELAY}/healthz`, 30000);
  await waitHealthy(`${INDEXER}/healthz`, 30000);

  const host = new LobbyClient(new RelayClient(RELAY));
  const tableId = await host.createTable(META);
  const [a, b] = await Promise.all([player(tableId, 'alice'), player(tableId, 'bob')]);
  assert.equal(a, b, 'players agree on the hand');
  console.log(`[validating-indexer-e2e] players' final stateHash: ${a.slice(0, 24)}…`);

  // The indexer accepted only AUTHENTIC signed envelopes; the transcript must be non-empty and
  // rebuild to the same state. An empty transcript would mean validation rejected real signatures.
  const indexer = new IndexerClient(INDEXER);
  const records = await indexer.records(tableId);
  console.log(`[validating-indexer-e2e] validated transcript records: ${records.length}`);
  assert.ok(records.length >= 8, `expected an authenticated transcript, got ${records.length} records`);
  const seats = [
    { seat: 0, stack: META.startingStack },
    { seat: 1, stack: META.startingStack },
  ];
  const rebuilt = rebuildHand(records, rulesetFromMeta(META), seats, 0, 0);
  assert.equal(rebuilt.stateHash, a, 'rebuilt-from-validated-transcript state matches the live players');
  assert.equal(rebuilt.state.handComplete, true);
  console.log('[validating-indexer-e2e] rebuilt from the VALIDATED transcript — matches live state.');

  // LEGALITY validation (audit #30): the indexer authenticates; this layer validates the authenticated
  // records form a LEGAL game through the canonical engine. The real transcript validates...
  const verdict = validateHandLegality(records, rulesetFromMeta(META), seats, 0, 0);
  assert.equal(verdict.valid, true, `the authenticated transcript must be legality-valid: ${verdict.reason}`);
  assert.equal(verdict.stateHash, a, 'the legality-validated state matches the live players');
  console.log('[validating-indexer-e2e] transcript LEGALITY-validated through the canonical engine.');

  // ...and a tampered record (an over-stack bet spliced into the real transcript) is REJECTED.
  const firstAction = records.find((r) => {
    try { return JSON.parse(Buffer.from(r.raw ?? '', 'base64').toString()).t === 'action'; } catch { return false; }
  });
  if (firstAction) {
    const env = JSON.parse(Buffer.from(firstAction.raw!, 'base64').toString());
    const tampered = records.map((r) =>
      r === firstAction ? { ...r, raw: Buffer.from(JSON.stringify({ ...env, kind: 'bet', amount: 10_000_000 })).toString('base64') } : r,
    );
    const bad = validateHandLegality(tampered, rulesetFromMeta(META), seats, 0, 0);
    assert.equal(bad.valid, false, 'an over-stack bet spliced into the transcript must be rejected');
    console.log(`[validating-indexer-e2e] illegal-action transcript REJECTED by legality validation: "${bad.reason}"`);
  }

  // Live fail-closed proof: a forged record (bad signature) for the registered table is REJECTED.
  const forged = {
    txid: 'forged-1',
    class: 'commit',
    tableId,
    raw: Buffer.from(JSON.stringify({ t: 'commit', seat: 0, hand: 999, c: 'deadbeef', sig: '00'.repeat(64) })).toString('base64'),
  };
  const res = await fetch(`${INDEXER}/ingest`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(forged),
  });
  assert.equal(res.status, 400, `forged record must be rejected with 400, got ${res.status}`);
  console.log('[validating-indexer-e2e] forged record rejected (400) — boundary is fail-closed.');

  console.log('\n[validating-indexer-e2e] PASS — authenticated ingest accepted real play and rejected a forgery (audit 7).');
}

main().then(
  () => {
    for (const c of children) c.kill();
    process.exit(0);
  },
  (e) => {
    console.error('[validating-indexer-e2e] FAIL:', (e as Error).message);
    for (const c of children) c.kill();
    process.exit(1);
  },
);
