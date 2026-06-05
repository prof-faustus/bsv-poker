# `@bsv-poker/protocol-types`

The lowest layer: canonical byte-exact **serialization**, deterministic **SHA-256**, the shared
domain types (cards, ruleset, actions, state, tx), and — security-critically — the **hardened
primitives** every parser and cryptographic comparison in the system depends on.

If you are auditing this codebase, start here and in `tx-builder` (the worked parser reference):
this package is where the defect classes are removed by construction.

## WHAT

| Module | What |
|---|---|
| `serialize.ts` | `ByteWriter` (LE, range-checked) and the **strict** `hexToBytes`/`bytesToHex`. |
| `safe.ts` | `tryHexToBytes`, `constantTimeEqualBytes`/`Hex`, `safeJsonParse`, `cryptoRandomBytes`/`randomId`, `readDerEcdsaSig`. |
| `reader.ts` | `ByteReader` — bounds-checked, non-throwing read cursor (inverse of `ByteWriter`). |
| `sha256.ts` | Portable, deterministic SHA-256 (runs identically in Node and the browser). |
| `cards.ts`/`ruleset.ts`/`actions.ts`/`state.ts`/`tx.ts` | The shared protocol types. |

## WHY

These primitives exist so that the corresponding defect CLASSES are impossible by construction:
silent hex misparse, out-of-bounds reads, timing oracles on secrets, JSON resource exhaustion, and
predictable randomness. Each is documented inline (full WHAT/HOW/WHY/boundary headers in `safe.ts`
and `reader.ts`) and is the single approved way to do that operation everywhere in the tree.

## Security & invariants

- [`SECURITY.md`](./SECURITY.md) — attacker model, trust boundary, what-breaks-if-violated.
- [`INVARIANTS.md`](./INVARIANTS.md) — every claim mapped to its positive/negative/fuzz test.

## Tests

```
node --test "packages/protocol-types/test/**/*.test.ts"
```

`safe.test.ts` and `reader.test.ts` run 250k+ fuzz iterations proving no primitive throws or reads
out of bounds on hostile input.
