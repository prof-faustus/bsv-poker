/**
 * Sighash interop check (core §6.8) — proves the platform's BIP-143 (FORKID) sighash matches
 * **bitcoinx** (the library a production BSV node validates with) byte-for-byte. If they match, a
 * platform-signed spend is node-acceptable: the on-chain last-mile is closed.
 *
 * STANDALONE: the bitcoinx reference values are COMMITTED VECTORS (frozen below) — an independent
 * implementation's output captured once on the deterministic cases — so the cross-check needs NO
 * external runtime. This is the standard committed-cross-implementation-test-vector approach: the
 * independence is in the frozen data, not a live process.
 *
 * To re-derive the vectors (if a case changes), run `tools/_sighash_ref.py` (bitcoinx) once on the
 * deterministic cases and paste its output into REF below. The cases use FIXED pubkeys so the
 * vectors are stable across runs.
 */

import assert from 'node:assert/strict';
import { bytesToHex, hexToBytes, hash256, type BranchBinding } from '@bsv-poker/protocol-types';
import { foldLocking, settlementLocking, type Script } from '@bsv-poker/script-templates-ts';
import { type Tx, buildFold, buildFunding, bip143Preimage } from '@bsv-poker/tx-builder';

// Fixed valid compressed secp256k1 points (G and 2G). The sighash hashes the scriptCode BYTES, so the
// points only need to be 33-byte well-formed pubkeys; fixing them makes the cases (and the committed
// bitcoinx reference vectors) deterministic.
const PUB1 = hexToBytes('0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798');
const PUB2 = hexToBytes('02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5');
const BIND: BranchBinding = {
  gid: '11'.repeat(8),
  rulesetHash: '22'.repeat(32),
  round: 0,
  stateHash: '33'.repeat(32),
  actingSeat: 0,
  successorCommitment: '44'.repeat(32),
};

/**
 * Committed bitcoinx reference sighashes for the deterministic cases below (captured from
 * tools/_sighash_ref.py once). Each is double-SHA256(BIP-143 FORKID preimage). The platform's
 * sighash MUST equal these — an independent-implementation cross-check, frozen as data.
 */
const REF: Record<string, string> = {
  'single in/out': 'f331a1dbf96ae33941d2397e6e9831e20dd95a1437b8d2a98bdee481bcbe7e50',
  'nonfinal seq + locktime': 'f572c63dfb80ec1b726c38e857e0fdae7e67fc587624fb231cc2de6f48a01bb5',
  'multi-output (funding + change)': 'ee0ef11775763b233317195d97289d6e5150d9708cc1f54a10b4cdaa50da1440',
  'two inputs (sign index 1)': '329373aa0cb9211df9b4cdef9531f8378d4215ad009dd19d1294b631d5f334e2',
};

function platformSighash(tx: Tx, index: number, scriptCode: Script, value: number): string {
  // double-SHA256(preimage) = the digest OP_CHECKSIG/ECDSA actually signs (see wire.ts).
  return bytesToHex(hash256(bip143Preimage(tx, index, scriptCode, value)));
}

interface Case {
  readonly name: string;
  readonly tx: Tx;
  readonly index: number;
  readonly scriptCode: Script;
  readonly value: number;
}

function cases(): Case[] {
  const sc = foldLocking(BIND, PUB1);
  const out = buildFold(BIND, PUB1);
  const base: Tx = {
    version: 1,
    inputs: [{ prevTxid: 'ab'.repeat(32), vout: 0, sequence: 0xffffffff }],
    outputs: [out],
    nLockTime: 0,
  };
  return [
    { name: 'single in/out', tx: base, index: 0, scriptCode: sc, value: 5000 },
    { name: 'nonfinal seq + locktime', tx: { ...base, inputs: [{ prevTxid: 'cd'.repeat(32), vout: 3, sequence: 0xfffffffe }], nLockTime: 850000 }, index: 0, scriptCode: sc, value: 999999 },
    {
      name: 'multi-output (funding + change)',
      tx: { ...base, outputs: [buildFunding(BIND, [PUB1, PUB2], 300), out] },
      index: 0,
      scriptCode: settlementLocking(BIND, PUB1),
      value: 12345,
    },
    {
      name: 'two inputs (sign index 1)',
      tx: {
        ...base,
        inputs: [
          { prevTxid: '01'.repeat(32), vout: 0, sequence: 0xffffffff },
          { prevTxid: '02'.repeat(32), vout: 7, sequence: 0xffffffff },
        ],
      },
      index: 1,
      scriptCode: sc,
      value: 2_000_000_000,
    },
  ];
}

function main(): void {
  let ok = 0;
  for (const c of cases()) {
    const mine = platformSighash(c.tx, c.index, c.scriptCode, c.value);
    const ref = REF[c.name];
    assert.ok(ref, `no committed bitcoinx vector for "${c.name}"`);
    const match = mine === ref;
    console.log(`[sighash-interop] ${match ? 'MATCH' : 'DIFF '} ${c.name}: ${mine.slice(0, 20)}…`);
    assert.equal(mine, ref, `sighash mismatch vs committed bitcoinx vector for "${c.name}"`);
    ok++;
  }
  console.log(`\n[sighash-interop] PASS — platform BIP-143 sighash matches the committed bitcoinx vectors for all ${ok} cases (node-acceptable). Standalone — no external runtime.`);
}

main();
