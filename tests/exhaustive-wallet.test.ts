/**
 * Exhaustive wallet-operation coverage. The human can fund, withdraw/defund, buy in, and cash out
 * in ANY order, at ANY time. This runs EVERY ordered sequence (length 1–4) over those operations
 * against an independent reference ledger and asserts: the balance always matches, never goes
 * negative, and an over-balance withdraw/buy-in fails WITHOUT moving money (and is recoverable).
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { WalletService } from '../packages/app-services/src/index.ts';

interface Op {
  readonly name: string;
  readonly amount: number;
  readonly delta: number; // effect on balance on success
  readonly canFail: boolean; // fails (throws) if amount > balance
  run(w: WalletService): Promise<void> | void;
}

const OPS: Op[] = [
  { name: 'add50', amount: 50, delta: +50, canFail: false, run: (w) => w.addFunds(50) },
  { name: 'withdraw30', amount: 30, delta: -30, canFail: true, run: (w) => w.withdraw(30, 'dest') },
  { name: 'buyIn40', amount: 40, delta: -40, canFail: true, run: (w) => void w.buyIn(40, 't') },
  { name: 'cashOut20', amount: 20, delta: +20, canFail: false, run: (w) => w.cashOut(20, 't') },
];

function sequences(depth: number): Op[][] {
  if (depth === 0) return [[]];
  const out: Op[][] = [];
  for (const head of OPS) for (const tail of sequences(depth - 1)) out.push([head, ...tail]);
  return out;
}

test('every wallet op sequence (len 1–4) matches a reference ledger; balance never negative', async () => {
  let seqs = 0;
  for (let depth = 1; depth <= 4; depth++) {
    for (const seq of sequences(depth)) {
      const w = new WalletService();
      let ref = 0;
      for (const op of seq) {
        if (op.canFail && ref < op.amount) {
          await assert.rejects(async () => op.run(w), `${seq.map((o) => o.name).join('>')} : ${op.name} must fail when balance ${ref} < ${op.amount}`);
          // balance unchanged after a failed op (no money moved)
          assert.equal(w.getBalance(), ref, `${op.name} changed balance on failure`);
        } else {
          await op.run(w);
          ref += op.delta;
          assert.equal(w.getBalance(), ref, `${seq.map((o) => o.name).join('>')} : balance drift`);
        }
        assert.ok(w.getBalance() >= 0, 'balance never negative');
      }
      seqs++;
    }
  }
  assert.equal(seqs, 4 + 16 + 64 + 256, 'covered every length 1–4 sequence');
});

test('non-positive funding amounts are rejected (no zero/negative money)', async () => {
  const w = new WalletService();
  await assert.rejects(async () => w.addFunds(0));
  await assert.rejects(async () => w.addFunds(-5));
  await w.addFunds(100);
  await assert.rejects(async () => w.withdraw(0, 'd'));
  assert.throws(() => w.buyIn(-1, 't'));
});
