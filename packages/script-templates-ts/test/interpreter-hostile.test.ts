/**
 * Script interpreter — hostile-input resource bounds (executable claims, INV-INT-*).
 *
 * The interpreter is the second hostile-input grammar of the system (the tx parser defers script
 * meaning to it). These tests prove a crafted/adversarial script cannot make it loop or allocate
 * without bound, and that NO script throws out of `evaluate`. Legitimate scripts (incl. real
 * multisig and the 256-bit in-script EC proof) are covered in templates.test.ts / shuffle-key.test.ts.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { OP, evaluate, type Script } from '../src/index.ts';

const ctx = { sighashPreimage: Uint8Array.of(1, 2, 3, 4) };

/** Encode a non-negative integer as a minimal little-endian script number (pushdata bytes). */
function scriptNum(n: number): Uint8Array {
  if (n === 0) return new Uint8Array(0);
  const out: number[] = [];
  let x = n;
  while (x > 0) {
    out.push(x & 0xff);
    x = Math.floor(x / 256);
  }
  if ((out[out.length - 1]! & 0x80) !== 0) out.push(0x00); // positive sign byte
  return Uint8Array.from(out);
}

// INV-INT-1 (NEGATIVE): OP_CHECKMULTISIG with an out-of-range pubkey count is rejected BEFORE any
// pop loop — proving the unbounded-pop DoS is closed. It must return quickly, not hang.
test('INV-INT-1 negative: OP_CHECKMULTISIG huge pubkey count rejected fast', () => {
  const script: Script = [scriptNum(1_000_000), OP.OP_CHECKMULTISIG];
  const t0 = Date.now();
  const r = evaluate(script, [], ctx);
  assert.equal(r.ok, false);
  assert.match(r.reason ?? '', /pubkey count out of range/);
  assert.ok(Date.now() - t0 < 1000, 'must reject without looping');
});

// INV-INT-2 (NEGATIVE): a pubkey count of 21 (one over the consensus cap of 20) is rejected.
test('INV-INT-2 negative: OP_CHECKMULTISIG pubkey count 21 rejected', () => {
  const r = evaluate([scriptNum(21), OP.OP_CHECKMULTISIG], [], ctx);
  assert.equal(r.ok, false);
  assert.match(r.reason ?? '', /pubkey count out of range/);
});

// INV-INT-3 (NEGATIVE): stack-depth flood (OP_1 x (MAX_STACK+2)) is rejected, never OOMs.
test('INV-INT-3 negative: stack depth limit enforced', () => {
  const flood: Script = Array.from({ length: 1002 }, () => OP.OP_1);
  const r = evaluate(flood, [], ctx);
  assert.equal(r.ok, false);
  assert.match(r.reason ?? '', /stack size limit/);
});

// INV-INT-4 (NEGATIVE): an oversized script number operand is rejected, not multiplied.
test('INV-INT-4 negative: oversized script number rejected', () => {
  const huge = new Uint8Array(5000); // > MAX_SCRIPT_NUM_BYTES (4096)
  huge[0] = 1;
  const r = evaluate([huge, huge, OP.OP_ADD], [], ctx);
  assert.equal(r.ok, false);
  assert.match(r.reason ?? '', /script number exceeds size limit/);
});

// INV-INT-5 (NEGATIVE): stack underflow on an empty stack is a clean failure, never a throw.
test('INV-INT-5 negative: underflow is a clean failure', () => {
  assert.doesNotThrow(() => {
    const r = evaluate([OP.OP_DUP], [], ctx);
    assert.equal(r.ok, false);
  });
});

// INV-INT-6 (FUZZ): no random script throws out of evaluate — only ok|fail values come out.
test('INV-INT-6 fuzz: 100k random scripts never throw', () => {
  let rng = 0x1234abcd;
  const next = (): number => (rng = (rng * 1103515245 + 12345) & 0x7fffffff);
  // A pool of real opcode bytes + occasional pushdata, so the fuzzer exercises real execution paths.
  for (let iter = 0; iter < 100_000; iter++) {
    const len = next() % 24;
    const script: Script = [];
    for (let i = 0; i < len; i++) {
      if (next() % 4 === 0) {
        const plen = next() % 6;
        const pd = new Uint8Array(plen);
        for (let j = 0; j < plen; j++) pd[j] = next() & 0xff;
        script.push(pd);
      } else {
        script.push((next() & 0xff) as unknown as number); // a random opcode byte
      }
    }
    assert.doesNotThrow(() => {
      const r = evaluate(script, [], ctx);
      assert.equal(typeof r.ok, 'boolean');
    });
  }
});
