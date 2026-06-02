import { test } from 'node:test';
import assert from 'node:assert/strict';
import { runCTConformance } from '@bsv-poker/adapters/conformance';
import {
  makeRealCT,
  canonicalPartyOrder,
  permutationFromEntropy,
  composePermutations,
  combinedKey,
  combinedSeed,
} from '../src/index.ts';

// REQ-DEP-003 / REQ-DEP-004: the real CT passes the SAME conformance suite as the fake, and
// the security-critical paths (shuffle, reveal, combined keys) are exercised on REAL crypto.
test('real CT passes the CT conformance suite', async () => {
  await runCTConformance(makeRealCT());
});

test('canonical party order is lexicographic by compressed pubkey (REQ-CRYPTO-003)', () => {
  assert.deepEqual(canonicalPartyOrder(['03ff', '02aa', '02ab']), ['02aa', '02ab', '03ff']);
});

test('permutation is a genuine permutation of [0..n)', () => {
  const perm = permutationFromEntropy(Uint8Array.from([1, 2, 3, 4]), 52);
  assert.equal(new Set(perm).size, 52);
  assert.deepEqual([...perm].sort((a, b) => a - b), Array.from({ length: 52 }, (_, i) => i));
});

test('shuffle composes secret permutations (INV-CT-1): order depends on every party', () => {
  const a = permutationFromEntropy(Uint8Array.from([1, 1, 1, 1]), 10);
  const b = permutationFromEntropy(Uint8Array.from([2, 2, 2, 2]), 10);
  const composedAB = composePermutations([a, b], 10);
  const composedA = composePermutations([a], 10);
  // adding party b's secret permutation changes the order (no single party fixes it)
  assert.notDeepEqual(composedAB, composedA);
  // composition is itself a permutation
  assert.equal(new Set(composedAB).size, 10);
});

test('combined keys are REAL secp256k1 compressed points (33 bytes, 02/03 prefix)', () => {
  const seed = combinedSeed([Uint8Array.from([7, 7, 7, 7]), Uint8Array.from([8, 8, 8, 8])]);
  const q0 = combinedKey(seed, 0);
  const q1 = combinedKey(seed, 1);
  assert.equal(q0.length, 66); // 33 bytes hex
  assert.match(q0, /^0[23]/); // compressed prefix
  assert.notEqual(q0, q1);
});
