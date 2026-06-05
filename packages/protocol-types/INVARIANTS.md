# `@bsv-poker/protocol-types` — Invariants (executable claims)

Every security claim about the hardened primitives, mapped to its proof. Run:

```
node --test "packages/protocol-types/test/**/*.test.ts"
```

## Strict hex + safe primitives — `test/safe.test.ts`

| ID | Claim | Proof (test name) |
|---|---|---|
| INV-HEX-1 | `hexToBytes` accepts valid hex and round-trips `bytesToHex`. | `hexToBytes accepts valid hex (round-trips bytesToHex)` |
| INV-HEX-2 | `hexToBytes` REJECTS the silent-truncation class (`'0g'`, `'0x10'`, etc.). | `hexToBytes REJECTS the silent-truncation class (parseInt("0g")=0)` |
| INV-HEX-3 | `hexToBytes` rejects odd-length / non-string. | `hexToBytes rejects odd-length and non-string` |
| INV-HEX-4 | `tryHexToBytes` NEVER throws — null on any malformed input. | `tryHexToBytes never throws — returns null on any malformed input` |
| INV-HEX-5 | FUZZ: `hexToBytes`/`tryHexToBytes` never throw uncaught and agree on valid input (100k). | `FUZZ: hexToBytes/tryHexToBytes never throw uncaught and agree on valid input (100k)` |
| INV-CT-1 | `constantTimeEqualBytes` is correct over equal/unequal/length-mismatch. | `constantTimeEqualBytes is correct (equal / unequal / length mismatch)` |
| INV-CT-2 | `constantTimeEqualHex` matches case-insensitively and rejects malformed. | `constantTimeEqualHex matches case-insensitively and rejects malformed` |
| INV-JSON-1 | `safeJsonParse` accepts small well-formed JSON. | `safeJsonParse accepts small well-formed JSON` |
| INV-JSON-2 | `safeJsonParse` rejects oversize input before parsing. | `safeJsonParse rejects oversize input before parsing` |
| INV-JSON-3 | `safeJsonParse` rejects over-deep nesting without a stack overflow. | `safeJsonParse rejects over-deep nesting (no stack overflow)` |
| INV-JSON-4 | `safeJsonParse` rejects over-wide structures (node bound). | `safeJsonParse rejects over-wide structures (node bound)` |
| INV-JSON-5 | `safeJsonParse` never throws on malformed / non-string input. | `safeJsonParse never throws on malformed / non-string input` |
| INV-JSON-6 | FUZZ: `safeJsonParse` never throws on random byte strings (50k). | `FUZZ: safeJsonParse never throws on random byte strings (50k)` |
| INV-DER-1 | `readDerEcdsaSig` parses well-formed and rejects malformed frames. | `readDerEcdsaSig parses a well-formed frame and rejects malformed ones` |
| INV-DER-2 | FUZZ: `readDerEcdsaSig` never reads out of bounds on random buffers (100k). | `FUZZ: readDerEcdsaSig never reads out of bounds on random buffers (100k)` |
| INV-RND-1 | `cryptoRandomBytes` returns the requested length and rejects bad sizes. | `cryptoRandomBytes returns the requested length and rejects bad sizes` |
| INV-RND-2 | `randomId` is hex, correct length, and unique across draws. | `randomId is hex, correct length, and unique across draws` |

## ByteReader (bounds-checked cursor) — `test/reader.test.ts`

See [INV-READ-1..7](../tx-builder/INVARIANTS.md) — the reader's invariants are listed there alongside
the parser that consumes it; the tests live in `protocol-types/test/reader.test.ts`.

## Canonical serialization — `test/serialize.test.ts`, `test/cards.test.ts`

| ID | Claim | Proof |
|---|---|---|
| INV-SER-1 | `ByteWriter` integers are little-endian and range-checked (u8/u16/u32/u64). | `serialize.test.ts` |
| INV-SER-2 | `serializeRuleset`/`rulesetHash` are deterministic and byte-exact (P2). | `serialize.test.ts` |
