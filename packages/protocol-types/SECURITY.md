# `@bsv-poker/protocol-types` — Security model

Reference cryptographic infrastructure. This is the lowest layer: the canonical serialization, the
deterministic SHA-256, and — security-critically — the **hardened primitives** every parser and
crypto comparison in the system is built on:

- `serialize.ts` — `ByteWriter` and the **strict hex codec** `hexToBytes`/`bytesToHex`.
- `safe.ts` — `tryHexToBytes`, `constantTimeEqualBytes`/`constantTimeEqualHex`, `safeJsonParse`,
  `cryptoRandomBytes`/`randomId`, `readDerEcdsaSig`.
- `reader.ts` — `ByteReader`, the bounds-checked, non-throwing read cursor.

These exist so that whole defect CLASSES are impossible by construction rather than patched per call
site. If this layer is wrong, everything above it is wrong.

## Attacker model

A funded adversary supplies every external byte the system parses: hex strings, JSON frames, DER
signatures, and the bytes a `ByteReader` walks. The defended goals:

1. **Silent misparse** — e.g. `parseInt('0g',16)=0` mapping malformed hex to `0x00`, diverging a
   determinism-critical binding. Closed by the strict nibble-validating `hexToBytes`.
2. **Out-of-bounds read** off an attacker length field. Closed by `ByteReader` (every read bounds-
   checked, `null` on short read) and `readDerEcdsaSig` (every interior length checked before index).
3. **Timing oracle** on a secret/commitment/MAC/token comparison (CWE-208/697). Closed by
   `constantTimeEqual*`.
4. **Resource exhaustion** via a huge/deep JSON frame (CWE-400). Closed by `safeJsonParse`
   (size + depth + node bounds, never throws).
5. **Predictable "random"** for a security value (CWE-338). Closed by `cryptoRandomBytes`/`randomId`
   (CSPRNG only, fail-closed — no `Math.random`).

## Trust boundary

| | |
|---|---|
| **Trusted inputs** | none. Every argument is treated as hostile. |
| **Untrusted inputs** | all hex/JSON/DER/byte inputs. |
| **Recoverable errors** | malformed hex (`tryHexToBytes`→null; `hexToBytes`→SyntaxError for trusted callers), short read (`ByteReader`→null), malformed/oversize JSON (`safeJsonParse`→`{ok:false}`), malformed DER (`readDerEcdsaSig`→null). |
| **Fatal errors** | absence of a CSPRNG (`cryptoRandomBytes` throws — fail-closed; there is no safe fallback). |
| **Side effects** | none. All primitives are pure except `cryptoRandomBytes` (reads the platform CSPRNG). |
| **State mutation** | none (a `ByteReader`'s private cursor only). |

## What must never be assumed

- Never assume a hex string from outside is valid — use `tryHexToBytes` at boundaries.
- Never compare a secret/commitment/MAC/token with `===` — use `constantTimeEqualHex`/`Bytes`.
- Never `JSON.parse` an external frame directly — use `safeJsonParse`.
- Never use `Math.random` for any value an adversary benefits from predicting — it is banned and
  CI-enforced (`tools/lint-security.ts`); use `cryptoRandomBytes`/`randomId`.
- Never ignore a `null`/`{ok:false}` return — that is the rejection, and it must fail closed.

## What breaks if these rules are violated

A single `===` on a commitment leaks a timing oracle; a single direct `JSON.parse` reopens the DoS
surface; a single `Math.random` makes a "secret" guessable; a single ignored `null` from `ByteReader`
desynchronises a parser into an OOB read. These are the exact classes this layer removes.
