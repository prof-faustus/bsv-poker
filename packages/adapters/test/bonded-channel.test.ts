/**
 * BondedChannel — in-tree bonded sub-satoshi channel. Executable claims (INV-BS-*).
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BondedChannel, reconcileQstar } from '../src/bonded-channel.ts';
import { RegtestNode } from '../src/regtest-node.ts';

// INV-BS-1 (positive): Q* close pays WHOLE satoshis that conserve the funded total + bonds.
test('INV-BS-1: cooperative close pays whole satoshis conserving funded + bonds', async () => {
  const ch = new BondedChannel();
  await ch.open({ parties: 2, k: 1000, funded: 10, bond: 1 });
  ch.transfer([[0, 1, 2500], [0, 1, 1500]]); // 4000 sub-units 0→1
  const r = await ch.close();
  assert.equal(r.payouts.length, 2);
  assert.ok(r.payouts.every((x) => Number.isInteger(x)), 'whole satoshis only');
  assert.equal(r.payouts.reduce((a, b) => a + b, 0), r.totalSettled);
  assert.equal(r.totalSettled, 10 + 2 * 1, 'funded(10) + bonds(2) conserved');
  assert.deepEqual(r.payouts, [7, 5]); // 6000→6 +bond, 4000→4 +bond
});

// INV-BS-1 (property): reconcileQstar always yields whole sats summing to the funded total.
test('INV-BS-1 property: Q* apportionment sums exactly to the funded total (no fractional output)', () => {
  let rng = 0x13572468;
  const next = (): number => (rng = (rng * 1103515245 + 12345) & 0x7fffffff);
  for (let iter = 0; iter < 5000; iter++) {
    const k = 1 + (next() % 1000);
    const funded = next() % 50;
    const parties = 2 + (next() % 4);
    // random sub-unit split of funded*k across parties
    const total = funded * k;
    const bal = new Array(parties).fill(0);
    let left = total;
    for (let i = 0; i < parties - 1; i++) { const give = next() % (left + 1); bal[i] = give; left -= give; }
    bal[parties - 1] = left;
    const q = reconcileQstar(bal, k);
    assert.ok(q.every((x) => Number.isInteger(x) && x >= 0));
    assert.equal(q.reduce((a, b) => a + b, 0), funded, 'whole-sat payouts sum to the funded total');
  }
});

// INV-BS-2 (negative): a contested close forfeits exactly the 1-sat bond.
test('INV-BS-2: contested close forfeits exactly the 1-sat bond', async () => {
  const ch = new BondedChannel();
  await ch.open({ parties: 2, k: 1000, funded: 10, bond: 1 });
  assert.match(ch.contested(1), /bond forfeited: 1 satoshi/i);
});

// INV-BS-3 (negative): an overdraw transfer is rejected; balances conserve the sub-unit supply.
test('INV-BS-3: overdraw rejected; transfers conserve the sub-unit supply', async () => {
  const ch = new BondedChannel();
  await ch.open({ parties: 2, k: 100, funded: 5, bond: 1 }); // party 0 holds 500 sub-units
  assert.throws(() => ch.transfer([[0, 1, 501]]), /insufficient|overdraw/);
  const v = ch.transfer([[0, 1, 200]]);
  assert.ok(v >= 1);
  const r = await ch.close();
  assert.equal(r.totalSettled, 5 + 2 * 1); // still conserves funded + bonds
});

// INV-BS-4 (positive): the cooperative close settles ON-CHAIN on the in-tree node.
test('INV-BS-4: close settles on-chain through the in-tree node', async () => {
  const node = new RegtestNode();
  const ch = new BondedChannel(node);
  await ch.open({ parties: 2, k: 1000, funded: 10, bond: 1 });
  ch.transfer([[0, 1, 3000]]);
  const r = await ch.close(); // throws if the node rejects the close tx
  assert.ok(r.txSizeBytes > 0, 'a real close tx was built + submitted');
  assert.equal(r.payouts.reduce((a, b) => a + b, 0), r.totalSettled);
});
