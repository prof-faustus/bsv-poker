/**
 * parseTxWire — executable security claims (see tx-builder/INVARIANTS.md, INV-TXP-*).
 *
 * This is the WORKED REFERENCE test set: every documented security property of the parser is here
 * as a positive case, the enumerated hostile/negative cases, and a fuzz case. A reviewer can read a
 * claim in INVARIANTS.md and jump straight to the test named for it.
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ByteWriter, bytesToHex } from '@bsv-poker/protocol-types';
import { parseTxWire, serializeParsedTx, type ParsedTx } from '../src/parse.ts';

// ---- helpers ---------------------------------------------------------------

/** Build a minimal but valid 1-in/1-out transaction as canonical wire bytes. */
function sampleTxBytes(): Uint8Array {
  const w = new ByteWriter();
  w.u32(2); // version
  w.u8(1); // vin count
  for (let i = 0; i < 32; i++) w.u8(i); // prevTxid (LE on wire)
  w.u32(0); // vout
  w.u8(3).u8(0x51).u8(0x52).u8(0x53); // scriptSig len 3 + bytes
  w.u32(0xffffffff); // sequence
  w.u8(1); // vout count
  w.u64(123456789012345n); // value (> 2^32, exercises u64)
  w.u8(2).u8(0x6a).u8(0x00); // script len 2 + bytes
  w.u32(0); // nLockTime
  return w.toBytes();
}

/** A structurally-random but canonical ParsedTx (for round-trip fuzzing). */
function randomParsedTx(next: () => number): ParsedTx {
  const mkScript = (): Uint8Array => {
    const n = next() % 6;
    const s = new Uint8Array(n);
    for (let i = 0; i < n; i++) s[i] = next() & 0xff;
    return s;
  };
  const nin = next() % 3;
  const nout = next() % 3;
  const inputs = Array.from({ length: nin }, () => {
    const t = new Uint8Array(32);
    for (let i = 0; i < 32; i++) t[i] = next() & 0xff;
    return { prevTxid: bytesToHex(t), vout: next() >>> 0, scriptSig: mkScript(), sequence: next() >>> 0 };
  });
  const outputs = Array.from({ length: nout }, () => ({
    // exercise the full u64 range, including values above 2^53
    satoshis: (BigInt(next()) << 32n) | BigInt(next() >>> 0),
    script: mkScript(),
  }));
  return { version: next() >>> 0, inputs, outputs, nLockTime: next() >>> 0, byteLength: 0 };
}

// ============================================================================
// POSITIVE
// ============================================================================

// INV-TXP-1: a canonical transaction parses into the exact structural fields.
test('INV-TXP-1 positive: parses a canonical tx and exposes correct fields', () => {
  const res = parseTxWire(sampleTxBytes());
  assert.ok(res.ok, res.ok ? '' : res.reason);
  if (!res.ok) return;
  assert.equal(res.tx.version, 2);
  assert.equal(res.tx.inputs.length, 1);
  assert.equal(res.tx.inputs[0]!.vout, 0);
  assert.equal(res.tx.inputs[0]!.sequence, 0xffffffff);
  assert.equal(res.tx.inputs[0]!.scriptSig.length, 3);
  assert.equal(res.tx.outputs.length, 1);
  assert.equal(res.tx.outputs[0]!.satoshis, 123456789012345n); // exact, not truncated
  assert.equal(res.tx.byteLength, sampleTxBytes().length);
  // prevTxid is the big-endian display reversal of the wire (00..1f → 1f..00)
  assert.equal(res.tx.inputs[0]!.prevTxid.slice(0, 2), '1f');
});

// INV-TXP-2: satoshi values above 2^53 survive a round-trip exactly (CWE-190).
test('INV-TXP-2 positive: u64 satoshis above 2^53 are exact', () => {
  const tx: ParsedTx = {
    version: 1,
    inputs: [],
    outputs: [{ satoshis: 0xfffffffffffffffen, script: new Uint8Array(0) }],
    nLockTime: 0,
    byteLength: 0,
  };
  const res = parseTxWire(serializeParsedTx(tx));
  assert.ok(res.ok);
  if (res.ok) assert.equal(res.tx.outputs[0]!.satoshis, 0xfffffffffffffffen);
});

// INV-TXP-3: round-trip identity — serialize∘parse and parse∘serialize are inverses on canonical tx.
test('INV-TXP-3 positive: round-trip identity on a canonical tx', () => {
  const bytes = sampleTxBytes();
  const res = parseTxWire(bytes);
  assert.ok(res.ok);
  if (!res.ok) return;
  assert.deepEqual(serializeParsedTx(res.tx), bytes, 'serialize(parse(b)) === b for canonical b');
});

// ============================================================================
// NEGATIVE — each is a named, enumerated rejection condition (fail-closed)
// ============================================================================

test('INV-TXP-N1 negative: non-Uint8Array input is rejected, not thrown', () => {
  const res = parseTxWire('deadbeef' as unknown as Uint8Array);
  assert.equal(res.ok, false);
});

test('INV-TXP-N2 negative: oversize input rejected before parsing', () => {
  const res = parseTxWire(new Uint8Array(100), { maxBytes: 10 });
  assert.equal(res.ok, false);
});

test('INV-TXP-N3 negative: too-short buffer rejected', () => {
  assert.equal(parseTxWire(new Uint8Array(5)).ok, false);
});

test('INV-TXP-N4 negative: truncated version', () => {
  assert.equal(parseTxWire(Uint8Array.from([1, 2, 3])).ok, false);
});

test('INV-TXP-N5 negative: truncation at every field boundary is rejected', () => {
  const full = sampleTxBytes();
  // Truncating at any length < full must fail (never an OOB read, never a partial success).
  for (let cut = 1; cut < full.length; cut++) {
    const res = parseTxWire(full.slice(0, cut));
    assert.equal(res.ok, false, `prefix of length ${cut} must be rejected`);
  }
});

test('INV-TXP-N6 negative: trailing bytes after nLockTime rejected (malleability)', () => {
  const withTrailer = Uint8Array.from([...sampleTxBytes(), 0x00]);
  const res = parseTxWire(withTrailer);
  assert.equal(res.ok, false);
  if (!res.ok) assert.match(res.reason, /trailing/);
});

test('INV-TXP-N7 negative: non-minimal CompactSize rejected (malleability)', () => {
  // vin count encoded as 0xfd 0x01 0x00 (value 1, but in the 3-byte form — non-minimal).
  const w = new ByteWriter();
  w.u32(2); // version
  w.u8(0xfd).u16(1); // NON-MINIMAL vin count = 1
  w.u32(0); // filler so the buffer clears the 10-byte minimum and reaches the varint check
  const res = parseTxWire(w.toBytes());
  assert.equal(res.ok, false);
  if (!res.ok) assert.match(res.reason, /non-minimal/);
});

test('INV-TXP-N8 negative: count exceeding remaining bytes rejected without large work', () => {
  // vin count = 0xfe 0xffffffff (4.29e9) with no inputs following.
  const w = new ByteWriter();
  w.u32(2);
  w.u8(0xfe).u32(0xffffffff);
  w.u8(0); // filler to clear the 10-byte minimum so the count check is what rejects
  const res = parseTxWire(w.toBytes());
  assert.equal(res.ok, false);
  if (!res.ok) assert.match(res.reason, /exceeds remaining/);
});

test('INV-TXP-N9 negative: script length exceeding remaining bytes rejected', () => {
  const w = new ByteWriter();
  w.u32(2); // version
  w.u8(1); // vin count
  for (let i = 0; i < 32; i++) w.u8(0); // prevTxid
  w.u32(0); // vout
  w.u8(0xfe).u32(0xffffffff); // scriptSig length = 4.29e9 (impossible)
  const res = parseTxWire(w.toBytes());
  assert.equal(res.ok, false);
  if (!res.ok) assert.match(res.reason, /exceeds remaining/);
});

// ============================================================================
// FUZZ
// ============================================================================

// INV-TXP-F1: no random byte string ever throws or OOB-reads — only ok|fail values come out.
test('INV-TXP-F1 fuzz: 200k random buffers never throw, only return a result', () => {
  let rng = 0xdeadbeef;
  const next = (): number => (rng = (rng * 1103515245 + 12345) & 0x7fffffff);
  for (let iter = 0; iter < 200_000; iter++) {
    const len = next() % 80;
    const buf = new Uint8Array(len);
    for (let i = 0; i < len; i++) buf[i] = next() & 0xff;
    assert.doesNotThrow(() => {
      const res = parseTxWire(buf);
      assert.equal(typeof res.ok, 'boolean');
      if (res.ok) {
        // any "ok" parse must be self-consistent: re-serialising reproduces the SAME bytes
        // (identity), proving the parse consumed exactly the buffer with no ambiguity.
        assert.deepEqual(serializeParsedTx(res.tx), buf);
        assert.equal(res.tx.byteLength, buf.length);
      }
    });
  }
});

// INV-TXP-F2: parse is the exact inverse of serialize on structurally-random canonical txs.
test('INV-TXP-F2 fuzz: 50k random canonical txs round-trip exactly', () => {
  let rng = 0x12345;
  const next = (): number => (rng = (rng * 1103515245 + 12345) & 0x7fffffff);
  for (let iter = 0; iter < 50_000; iter++) {
    const tx = randomParsedTx(next);
    const bytes = serializeParsedTx(tx);
    const res = parseTxWire(bytes);
    assert.ok(res.ok, res.ok ? '' : `${res.reason} @${res.offset}`);
    if (!res.ok) return;
    assert.deepEqual(serializeParsedTx(res.tx), bytes);
    assert.equal(res.tx.inputs.length, tx.inputs.length);
    assert.equal(res.tx.outputs.length, tx.outputs.length);
    for (let i = 0; i < tx.outputs.length; i++) {
      assert.equal(res.tx.outputs[i]!.satoshis, tx.outputs[i]!.satoshis);
    }
  }
});
