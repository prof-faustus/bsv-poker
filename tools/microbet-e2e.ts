/**
 * Micro-betting E2E (app §A23, REQ-WALLET-005, REQ-DEP-004) against the project's OWN in-tree bonded
 * sub-satoshi channel (`@bsv-poker/adapters/bonded-channel`) settling on the in-tree node — STANDALONE,
 * no external process. Open a 2-party channel (k sub-units, 1-sat bond), apply sub-satoshi transfers,
 * cooperatively close with WHOLE-satoshi Q* settlement (no fractional output ever — INV-BS-1), and
 * demonstrate a contested close forfeiting the offender's fixed 1-sat bond (INV-BS-2).
 */

import assert from 'node:assert/strict';
import { RegtestNode } from '@bsv-poker/adapters/regtest-node';
import { BondedChannel } from '@bsv-poker/adapters/bonded-channel';

async function main(): Promise<void> {
  const node = new RegtestNode(); // the in-tree node — open/close settle on-chain through it
  const ch = new BondedChannel(node);

  console.log('[microbet-e2e] opening an in-tree bonded sub-sat channel: 2 parties, k=1000, S=10, bond=1…');
  await ch.open({ parties: 2, k: 1000, funded: 10, bond: 1 });

  console.log('[microbet-e2e] applying sub-satoshi transfers (party 0 → party 1)…');
  const version = ch.transfer([
    [0, 1, 2500],
    [0, 1, 1500],
  ]);
  assert.ok(version >= 1, 'transfers advanced the channel version');
  console.log(`[microbet-e2e] channel version after transfers = ${version}`);

  console.log('[microbet-e2e] cooperative close with whole-satoshi Q* settlement (on-chain)…');
  const close = await ch.close();
  console.log(`[microbet-e2e] payouts (whole sats incl. bond) = [${close.payouts.join(', ')}]; total = ${close.totalSettled}; tx ${close.txSizeBytes} B`);
  assert.ok(close.payouts.length === 2, 'a payout per party');
  assert.ok(close.payouts.every((x) => Number.isInteger(x)), 'all payouts are WHOLE satoshis (INV-BS-1)');
  assert.equal(
    close.payouts.reduce((a, b) => a + b, 0),
    close.totalSettled,
    'payouts conserve the total settled',
  );

  console.log('[microbet-e2e] contested close (party 1 broadcasts a stale state → forfeits its bond)…');
  const out = ch.contested(1);
  assert.match(out, /bond forfeited: 1 satoshi/i);
  console.log('[microbet-e2e] ' + out.split('\n').find((l) => /bond forfeited/.test(l))!.trim());

  console.log('\n[microbet-e2e] PASS — in-tree bonded sub-sat channel: sub-satoshi transfers, whole-satoshi Q* close settled on the in-tree node, 1-sat bond forfeiture. Standalone — no external process.');
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('[microbet-e2e] FAIL:', (e as Error).message);
    process.exit(1);
  },
);
