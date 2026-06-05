/**
 * ByteReader — executable security claims (see protocol-types/INVARIANTS.md, INV-READ-*).
 *
 * Every claim below is stated, then proven with a positive case, the enumerated negative cases, and
 * a fuzz case. The reader's whole purpose is that NO input makes it read out of bounds or throw.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ByteReader } from '../src/reader.ts';

// INV-READ-1: fixed-width reads return the exact little-endian value and advance the cursor.
test('INV-READ-1 positive: u8/u16/u32/u64 LE values and cursor advance', () => {
  const r = new ByteReader(Uint8Array.from([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f]));
  assert.equal(r.tryReadU8(), 0x01);
  assert.equal(r.offset, 1);
  assert.equal(r.tryReadU16LE(), 0x0302);
  assert.equal(r.offset, 3);
  assert.equal(r.tryReadU32LE(), 0x07060504 >>> 0);
  assert.equal(r.offset, 7);
  assert.equal(r.tryReadU64LE(), 0x0f0e0d0c0b0a0908n);
  assert.equal(r.offset, 15);
  assert.equal(r.atEnd, true);
});

// INV-READ-2: u32 high bit is read UNSIGNED (never negative).
test('INV-READ-2 positive: u32 with high bit set is unsigned', () => {
  const r = new ByteReader(Uint8Array.from([0x00, 0x00, 0x00, 0x80]));
  assert.equal(r.tryReadU32LE(), 0x80000000);
});

// INV-READ-3: u64 above 2^53 is exact (bigint, never truncated). CWE-190.
test('INV-READ-3 positive: u64 above 2^53 is exact', () => {
  const r = new ByteReader(Uint8Array.from([0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]));
  assert.equal(r.tryReadU64LE(), 0xffffffffffffffffn);
});

// INV-READ-4 (NEGATIVE): a short read returns null and does NOT advance the cursor.
test('INV-READ-4 negative: short reads return null without advancing', () => {
  assert.equal(new ByteReader(new Uint8Array(0)).tryReadU8(), null);
  const r2 = new ByteReader(Uint8Array.from([0x01]));
  assert.equal(r2.tryReadU16LE(), null);
  assert.equal(r2.offset, 0, 'cursor must not move on a failed read');
  assert.equal(new ByteReader(Uint8Array.from([1, 2, 3])).tryReadU32LE(), null);
  assert.equal(new ByteReader(Uint8Array.from([1, 2, 3, 4, 5, 6, 7])).tryReadU64LE(), null);
});

// INV-READ-5 (NEGATIVE): tryReadBytes rejects bogus lengths and over-reads, returns a COPY.
test('INV-READ-5 negative: tryReadBytes bounds + copy semantics', () => {
  const buf = Uint8Array.from([1, 2, 3, 4]);
  const r = new ByteReader(buf);
  assert.equal(r.tryReadBytes(5), null); // more than remaining
  assert.equal(r.tryReadBytes(-1), null); // bogus length
  assert.equal(r.tryReadBytes(1.5), null); // non-integer
  const got = r.tryReadBytes(2)!;
  assert.deepEqual(got, Uint8Array.from([1, 2]));
  got[0] = 0xff; // mutate the returned copy
  assert.equal(buf[0], 1, 'returned slice must be a copy, not an alias');
});

// INV-READ-6: non-Uint8Array construction is a caller error (throws TypeError, not on hostile data).
test('INV-READ-6: constructor rejects non-Uint8Array', () => {
  assert.throws(() => new ByteReader('nope' as unknown as Uint8Array), TypeError);
});

// INV-READ-7 (FUZZ): no random buffer + read sequence ever throws or reads out of bounds.
test('INV-READ-7 fuzz: 200k random read sequences never throw / never over-read', () => {
  let rng = 0x9e3779b9;
  const next = (): number => (rng = (rng * 1103515245 + 12345) & 0x7fffffff);
  for (let iter = 0; iter < 200_000; iter++) {
    const len = next() % 24;
    const buf = new Uint8Array(len);
    for (let i = 0; i < len; i++) buf[i] = next() & 0xff;
    const r = new ByteReader(buf);
    assert.doesNotThrow(() => {
      // a random sequence of reads
      for (let step = 0; step < 8; step++) {
        const before = r.offset;
        switch (next() % 5) {
          case 0: r.tryReadU8(); break;
          case 1: r.tryReadU16LE(); break;
          case 2: r.tryReadU32LE(); break;
          case 3: r.tryReadU64LE(); break;
          default: r.tryReadBytes(next() % 30); break;
        }
        // the cursor never moves backwards and never past the buffer end
        assert.ok(r.offset >= before && r.offset <= buf.length);
      }
    });
  }
});
