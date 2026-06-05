# `@bsv-poker/tx-builder`

Construction, **wire serialization**, and **hostile-input parsing** of BSV transactions, plus the
BIP-143 (FORKID) sighash and the pre-signed timeout-default ("fallback") graph.

This package contains the project's **worked reference for parsing untrusted bytes**:
[`src/parse.ts`](./src/parse.ts) (`parseTxWire`) built on the bounds-checked
[`ByteReader`](../protocol-types/src/reader.ts). Read those two files and their tests first if you
are here to learn how this codebase writes a parser, or to attack one.

---

## WHAT this package does

| Module | What | Direction |
|---|---|---|
| `txbuilder.ts` | The in-memory `Tx`/`TxInput`/`TxOutput` model and helpers to build protocol transactions. | build |
| `wire.ts` | `serializeTxWire` (Tx → canonical bytes), `txidWire`, `bip143Preimage`, `sighashMessage`. | encode |
| `parse.ts` | `parseTxWire` (bytes → `ParsedTx`, **never throws**), `serializeParsedTx` (the structural inverse). | **decode** |
| `fallback.ts` | The pre-signed timeout-default refund graph (REQ-TX-008): each stake returns on a stall. | build |
| `timing.ts` | Byte-size / fee timing helpers. | util |

## HOW the parser works (the reference)

`parseTxWire` reads the transaction envelope through a single `ByteReader` whose every read is
bounds-checked and returns `null` on a short read. After every read the parser checks for `null` and
returns a precise `{ ok:false, reason, offset }`. It:

- bounds the total input size before starting (`maxBytes`, default 10 MiB);
- requires **minimal CompactSize** for every count/length (rejects non-canonical encodings);
- bounds every count/length by the **bytes that remain**, so the parse loop has a provable upper
  bound (NASA P10) and never over-allocates;
- carries satoshi values as **`bigint`** so the 8-byte value field is never truncated (CWE-190);
- **rejects trailing bytes** after `nLockTime`;
- returns raw script bytes (script decoding is the interpreter's separate, smaller grammar).

## WHY this package exists, and why these design choices

A transaction is the single most security-critical byte string in the system: it moves money. It
arrives from the network, a node RPC, a file, or a paste — all hostile. The classic Bitcoin parser
bugs are attacker-driven length fields (OOB reads), 8-byte value truncation, and malleability via
non-canonical encodings. `parseTxWire` closes each of those **by construction**, and the choices are
documented inline in `parse.ts` under "WHY (and why THIS design rather than the alternatives)",
including **why scripts are not decoded here** and **why BTC's segwit marker is not honoured**.

## Security

This is reference cryptographic infrastructure. See:

- [`SECURITY.md`](./SECURITY.md) — the trust boundary, trusted/untrusted inputs, recoverable vs
  fatal errors, every rejection condition, side effects, and the attacker model.
- [`INVARIANTS.md`](./INVARIANTS.md) — every security claim, each mapped to its **positive,
  negative, and fuzz** test by name.

## Public API (stable)

```ts
import {
  parseTxWire, serializeParsedTx,        // decode / re-encode (this package's reference parser)
  type ParsedTx, type TxParseResult,
  serializeTxWire, txidWire,             // encode
  bip143Preimage, sighashMessage, SIGHASH_ALL_FORKID,
} from '@bsv-poker/tx-builder';

const res = parseTxWire(rawBytes);
if (!res.ok) { /* FAIL CLOSED — res.reason / res.offset say why */ }
else { /* res.tx : ParsedTx (satoshis are bigint) */ }
```

## Tests

```
node --test "packages/tx-builder/test/**/*.test.ts"
node --test "packages/protocol-types/test/reader.test.ts"   # the ByteReader primitive
```

`parse.test.ts` and `reader.test.ts` are organised as **executable claims**: each test is named for
the invariant it proves (`INV-TXP-*`, `INV-READ-*`) so a reviewer can jump from `INVARIANTS.md` to
the proof. The fuzz tests run 200k+ iterations each and assert no input throws or OOB-reads, plus the
parse/serialize identity on every accepted input.
