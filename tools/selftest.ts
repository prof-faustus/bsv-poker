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
 * Phase-0 note: the local BSV node (bonded-subsat-channel, D6) is bound by the real adapter in
 * a later step; here the node/chain role is represented by the indexer projection + BS fake.
 */

import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { join } from 'node:path';
import { parseHand, type Card, type Ruleset, type Action } from '@bsv-poker/protocol-types';
import { createHoldem } from '@bsv-poker/game-holdem';

const ROOT = process.cwd();
const children: ChildProcess[] = [];

function goBuild(dir: string): void {
  const r = spawnSync('go', ['build', './...'], { cwd: join(ROOT, dir), stdio: 'inherit' });
  if (r.status !== 0) throw new Error(`go build failed in ${dir}`);
}

function startService(dir: string, addr: string): ChildProcess {
  const child = spawn('go', ['run', '.', '-addr', addr], {
    cwd: join(ROOT, dir),
    stdio: 'ignore',
  });
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
    console.log('[selftest] building Go services…');
    goBuild('apps/relay-go');
    goBuild('apps/indexer-go');

    console.log('[selftest] starting relay (:8091) and indexer (:8092)…');
    startService('apps/relay-go', '127.0.0.1:8091');
    startService('apps/indexer-go', '127.0.0.1:8092');
    await waitHealthy('http://127.0.0.1:8091/healthz', 30000);
    await waitHealthy('http://127.0.0.1:8092/healthz', 30000);
    console.log('[selftest] relay + indexer healthy.');

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
