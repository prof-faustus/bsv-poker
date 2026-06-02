import { test } from 'node:test';
import assert from 'node:assert/strict';
import { WalletService, type WalletState, type WalletPersistence } from '../src/wallet.ts';

test('add and remove funds; balance + history track correctly', async () => {
  const w = new WalletService();
  assert.equal(w.getBalance(), 0);
  await w.addFunds(1000);
  assert.equal(w.getBalance(), 1000);
  await w.addFunds(500, { memo: 'faucet' });
  assert.equal(w.getBalance(), 1500);
  await w.withdraw(400, 'addr-xyz');
  assert.equal(w.getBalance(), 1100);
  const h = w.state().history;
  assert.deepEqual(
    h.map((e) => [e.kind, e.amount, e.balanceAfter]),
    [
      ['deposit', 1000, 1000],
      ['deposit', 500, 1500],
      ['withdraw', 400, 1100],
    ],
  );
});

test('withdraw more than balance is rejected (fail-closed)', async () => {
  const w = new WalletService();
  await w.addFunds(100);
  await assert.rejects(w.withdraw(101, 'x'), /insufficient balance/);
  assert.equal(w.getBalance(), 100);
});

test('buy-in deducts and cash-out returns; net of a winning session is positive', async () => {
  const w = new WalletService();
  await w.addFunds(1000);
  const stack = w.buyIn(200, 'table-1'); // sit with 200
  assert.equal(stack, 200);
  assert.equal(w.getBalance(), 800);
  // ... play, leave with 260
  w.cashOut(260, 'table-1');
  assert.equal(w.getBalance(), 1060); // up 60 on the session
});

test('cannot buy in for more than the balance', async () => {
  const w = new WalletService();
  await w.addFunds(50);
  assert.throws(() => w.buyIn(60), /insufficient balance to buy in/);
});

test('amounts must be positive integers (no fractional satoshis, INV-BS-1)', async () => {
  const w = new WalletService();
  await assert.rejects(w.addFunds(0));
  await assert.rejects(w.addFunds(1.5));
  await assert.rejects(w.addFunds(-10));
});

test('persistence round-trips the balance + history', async () => {
  let saved: WalletState | null = null;
  const persistence: WalletPersistence = {
    load: () => saved,
    save: (s) => {
      saved = s;
    },
  };
  const w1 = new WalletService({ persistence });
  await w1.addFunds(777);
  w1.buyIn(100, 't');
  // a fresh wallet loads the persisted state
  const w2 = new WalletService({ persistence });
  assert.equal(w2.getBalance(), 677);
  assert.equal(w2.state().history.length, 2);
});

test('onChange fires on every funds movement', async () => {
  const w = new WalletService();
  let n = 0;
  w.onChange(() => (n += 1));
  await w.addFunds(10);
  w.buyIn(5);
  w.cashOut(5);
  assert.equal(n, 3);
});
