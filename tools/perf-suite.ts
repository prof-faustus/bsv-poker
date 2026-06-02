/**
 * Measured performance + bounded-memory suite (core §A17 Power-of-Ten; REQ-APP-090 round-trip
 * latency target, REQ-APP-092 bounded working memory in the hot path). Measures real operations on
 * the critical paths and asserts a latency budget + that the per-state hot path does NOT grow its
 * working set per iteration (no unbounded allocation / leak). Re-execs with --expose-gc so the
 * memory measurement is deterministic.
 */

import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { bestHigh } from '@bsv-poker/hand-eval';
import { deckFromEntropies } from '@bsv-poker/app-services';
import type { BranchBinding } from '@bsv-poker/protocol-types';
import { genKeyPair, signPreimage, fundingLocking, fundingUnlocking } from '@bsv-poker/script-templates-ts';
import { type Tx, buildSettlement, sighashMessage, SIGHASH_ALL_FORKID, serializeTxWire } from '@bsv-poker/tx-builder';

// Deterministic GC: re-exec once with --expose-gc if it isn't available.
if (typeof globalThis.gc !== 'function') {
  const r = spawnSync(process.execPath, ['--expose-gc', process.argv[1]!], { stdio: 'inherit' });
  process.exit(r.status ?? 1);
}
const gc = globalThis.gc as () => void;

const BIND: BranchBinding = { gid: 'a1'.repeat(8), rulesetHash: 'b2'.repeat(32), round: 0, stateHash: 'c3'.repeat(32), actingSeat: 0, successorCommitment: '00'.repeat(32) };

function medianMs(iters: number, fn: () => void): number {
  const samples: number[] = [];
  for (let i = 0; i < iters; i++) {
    const t = process.hrtime.bigint();
    fn();
    samples.push(Number(process.hrtime.bigint() - t) / 1e6);
  }
  samples.sort((a, b) => a - b);
  return samples[Math.floor(samples.length / 2)]!;
}

function main(): void {
  // 1) Mental-poker shuffle (compose 4 players' secret permutations into a 52-card deck).
  const entropies = Array.from({ length: 4 }, (_, i) => Uint8Array.from({ length: 32 }, (_, j) => (i * 31 + j * 7) & 0xff));
  const shuffleMs = medianMs(200, () => deckFromEntropies(entropies));

  // 2) Hand evaluation hot path (best 5 of 7).
  const seven = [0, 13, 26, 39, 4, 17, 51];
  const evalMs = medianMs(2000, () => bestHigh(seven));

  // 3) Local action round-trip: build → sighash → sign → assemble (UI action → signed spend).
  const a = genKeyPair();
  const b = genKeyPair();
  const lock = fundingLocking(BIND, [a.pubCompressed, b.pubCompressed]);
  const roundTripMs = medianMs(200, () => {
    const tx: Tx = { version: 1, inputs: [{ prevTxid: 'ab'.repeat(32), vout: 0, sequence: 0xffffffff }], outputs: [buildSettlement(BIND, a.pubCompressed, 1000)], nLockTime: 0 };
    const msg = sighashMessage(tx, 0, lock, 2000);
    const ss = fundingUnlocking([Uint8Array.from([...signPreimage(msg, a.priv), SIGHASH_ALL_FORKID]), Uint8Array.from([...signPreimage(msg, b.priv), SIGHASH_ALL_FORKID])]);
    serializeTxWire(tx, [ss]);
  });

  console.log(`[perf] shuffle(4p→52)=${shuffleMs.toFixed(3)}ms  handEval(7→best5)=${evalMs.toFixed(4)}ms  actionRoundTrip(2-of-2)=${roundTripMs.toFixed(3)}ms`);

  // Generous CI-stable latency budgets (REQ-APP-090: round-trip well under a human-perceptible bound).
  assert.ok(roundTripMs < 100, `action round-trip ${roundTripMs}ms exceeds 100ms budget`);
  assert.ok(evalMs < 5, `hand-eval ${evalMs}ms exceeds 5ms budget`);

  // REQ-APP-092: bounded working memory — the per-state hot path must not grow its working set.
  const M = 200_000;
  gc();
  const before = process.memoryUsage().heapUsed;
  let sink = 0;
  for (let i = 0; i < M; i++) sink ^= bestHigh(seven).value.category;
  gc();
  const grewBytes = process.memoryUsage().heapUsed - before;
  console.log(`[perf] bounded-memory: ${M} hot-path evals → retained heap Δ ${(grewBytes / 1024).toFixed(1)} KiB (sink=${sink})`);
  assert.ok(grewBytes < 4 * 1024 * 1024, `hot path retained ${grewBytes} bytes over ${M} iters — not bounded`);

  console.log('\n[perf] PASS — latency within budget and the state-derivation hot path holds bounded working memory (§A17 / REQ-APP-090/092).');
}

main();
