/**
 * Wallet add/remove-funds E2E (core §9, app §A6.2) with a REAL regtest deposit. Demonstrates the
 * live-ready funding seam: a node-backed FundingBackend whose deposit MINES a real regtest block
 * via the embedded BSV node (the player's coinbase). Then buy-in / cash-out / withdraw.
 *
 * The node daemon exposes mine/height (no balance/UTXO RPC yet), so the credited coinbase amount
 * is the regtest subsidy constant (TRACKED ASSUMPTION) — the REAL part is that the deposit
 * triggers an on-chain block via the node (height advances). Mainnet deposit/withdraw replace the
 * backend behind the same WalletService, with the research flag.
 */

import assert from 'node:assert/strict';
import { RegtestNode } from '@bsv-poker/adapters/regtest-node';
import { genKeyPair } from '@bsv-poker/script-templates-ts';
import { bytesToHex } from '@bsv-poker/protocol-types';
import { WalletService, type FundingBackend } from '@bsv-poker/app-services';

const REGTEST_SUBSIDY = 5_000_000_000; // sats per regtest coinbase (TRACKED ASSUMPTION)

async function main(): Promise<void> {
  const node = new RegtestNode();
  const payoutPub = bytesToHex(genKeyPair().pubCompressed);
  try {
    const deadline = Date.now() + 30000;
    while (!(await node.ping().catch(() => false))) {
      if (Date.now() > deadline) throw new Error('node did not start');
      await new Promise((r) => setTimeout(r, 400));
    }

    // Node-backed funding: deposit mines a real regtest coinbase to the player's key.
    const backend: FundingBackend = {
      async deposit() {
        await node.generateBlock(payoutPub);
      },
      async withdraw() {
        // real on-chain spend is the live seam (node tx-submit RPC pending); play-money debit here
      },
    };
    const wallet = new WalletService({ network: 'regtest', backend });

    const h0 = await node.height();
    console.log(`[wallet-e2e] node height before deposit = ${h0}; wallet balance = ${wallet.getBalance()}`);

    console.log('[wallet-e2e] ADD FUNDS → mines a real regtest block (coinbase to the player)…');
    await wallet.addFunds(REGTEST_SUBSIDY, { memo: 'regtest coinbase' });
    const h1 = await node.height();
    assert.equal(h1, h0 + 1, 'deposit mined exactly one real block');
    assert.equal(wallet.getBalance(), REGTEST_SUBSIDY);
    console.log(`[wallet-e2e] node height after deposit = ${h1}; wallet balance = ${wallet.getBalance()}`);

    console.log('[wallet-e2e] buy in 200 → play a session → cash out 260…');
    const stack = wallet.buyIn(200, 'table-1');
    assert.equal(stack, 200);
    wallet.cashOut(260, 'table-1');
    assert.equal(wallet.getBalance(), REGTEST_SUBSIDY + 60);

    console.log('[wallet-e2e] REMOVE FUNDS (withdraw 1000)…');
    await wallet.withdraw(1000, 'mxExternalRegtestAddr');
    assert.equal(wallet.getBalance(), REGTEST_SUBSIDY + 60 - 1000);

    console.log(`[wallet-e2e] history: ${wallet.state().history.map((e) => `${e.kind}:${e.amount}`).join(', ')}`);
    console.log('\n[wallet-e2e] PASS — wallet adds funds via a REAL regtest mine, buys in, cashes out, withdraws.');
  } finally {
    await node.shutdown();
  }
}

main().then(
  () => process.exit(0),
  (e) => {
    console.error('[wallet-e2e] FAIL:', (e as Error).message);
    process.exit(1);
  },
);
