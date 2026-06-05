# `@bsv-poker/tx-builder` — Invariants (executable claims)

Every security claim about the transaction parser is listed here and mapped to the test(s) that
**prove** it: a positive case, the enumerated negative/hostile cases, and a fuzz case. Tests are
named for their invariant so you can jump straight to the proof.

Run them:

```
node --test "packages/protocol-types/test/reader.test.ts"   # ByteReader primitive (INV-READ-*)
node --test "packages/tx-builder/test/parse.test.ts"         # parseTxWire (INV-TXP-*)
```

A claim is only "done" when its positive **and** its negative **and** (where a parser is involved)
its fuzz test all pass. 200k+ fuzz iterations run per parser.

---

## ByteReader (the bounds-checked primitive) — `protocol-types/src/reader.ts`

| ID | Claim | Proof (test name in `reader.test.ts`) |
|---|---|---|
| INV-READ-1 | Fixed-width reads return the exact little-endian value and advance the cursor by the width. | `INV-READ-1 positive: u8/u16/u32/u64 LE values and cursor advance` |
| INV-READ-2 | A u32 with the high bit set is read **unsigned** (never negative). | `INV-READ-2 positive: u32 with high bit set is unsigned` |
| INV-READ-3 | A u64 above 2^53 is exact (`bigint`), never truncated (CWE-190). | `INV-READ-3 positive: u64 above 2^53 is exact` |
| INV-READ-4 | A short read returns `null` and does **not** advance the cursor. | `INV-READ-4 negative: short reads return null without advancing` |
| INV-READ-5 | `tryReadBytes` rejects bogus/over-long lengths and returns a **copy** (no aliasing). | `INV-READ-5 negative: tryReadBytes bounds + copy semantics` |
| INV-READ-6 | Constructing from a non-`Uint8Array` is a caller error (TypeError), not a hostile-data path. | `INV-READ-6: constructor rejects non-Uint8Array` |
| INV-READ-7 | **No** random buffer + read sequence ever throws or moves the cursor past the end. | `INV-READ-7 fuzz: 200k random read sequences never throw / never over-read` |

## parseTxWire (the reference hostile-input parser) — `tx-builder/src/parse.ts`

### Positive

| ID | Claim | Proof (test name in `parse.test.ts`) |
|---|---|---|
| INV-TXP-1 | A canonical transaction parses into the exact structural fields (version, in/out, scripts, locktime, byteLength) with prevTxid exposed big-endian. | `INV-TXP-1 positive: parses a canonical tx and exposes correct fields` |
| INV-TXP-2 | Satoshi values above 2^53 survive a round-trip exactly (CWE-190). | `INV-TXP-2 positive: u64 satoshis above 2^53 are exact` |
| INV-TXP-3 | Round-trip identity: `serializeParsedTx(parseTxWire(b).tx) === b` for canonical `b`. | `INV-TXP-3 positive: round-trip identity on a canonical tx` |

### Negative (fail-closed; each is an enumerated rejection condition)

| ID | Claim | Proof |
|---|---|---|
| INV-TXP-N1 | Non-`Uint8Array` input is rejected, not thrown. | `INV-TXP-N1 negative: non-Uint8Array input is rejected, not thrown` |
| INV-TXP-N2 | Oversize input is rejected before parsing (CWE-400). | `INV-TXP-N2 negative: oversize input rejected before parsing` |
| INV-TXP-N3 | A too-short buffer is rejected. | `INV-TXP-N3 negative: too-short buffer rejected` |
| INV-TXP-N4 | A truncated version is rejected. | `INV-TXP-N4 negative: truncated version` |
| INV-TXP-N5 | Truncation at **every** field boundary is rejected (exhaustive over all prefixes). | `INV-TXP-N5 negative: truncation at every field boundary is rejected` |
| INV-TXP-N6 | Trailing bytes after `nLockTime` are rejected (malleability). | `INV-TXP-N6 negative: trailing bytes after nLockTime rejected` |
| INV-TXP-N7 | Non-minimal CompactSize is rejected (malleability). | `INV-TXP-N7 negative: non-minimal CompactSize rejected` |
| INV-TXP-N8 | A count exceeding the remaining bytes is rejected without large work. | `INV-TXP-N8 negative: count exceeding remaining bytes rejected without large work` |
| INV-TXP-N9 | A script length exceeding the remaining bytes is rejected. | `INV-TXP-N9 negative: script length exceeding remaining bytes rejected` |

### Fuzz

| ID | Claim | Proof |
|---|---|---|
| INV-TXP-F1 | No random byte string throws or OOB-reads; any accepted parse re-serializes to the **same** bytes (no ambiguity). | `INV-TXP-F1 fuzz: 200k random buffers never throw, only return a result` |
| INV-TXP-F2 | `parseTxWire` is the exact inverse of `serializeParsedTx` on structurally-random canonical transactions (incl. u64 values). | `INV-TXP-F2 fuzz: 50k random canonical txs round-trip exactly` |

---

## How to extend

When you add a parser rule, add: (1) the rule in `parse.ts` with its WHY, (2) a row here, (3) a
positive test, (4) the negative test(s) for the inputs it now rejects, and (5) make sure the fuzz
tests still pass. A rule with no negative test is not considered enforced.
