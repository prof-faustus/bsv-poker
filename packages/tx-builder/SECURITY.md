# `@bsv-poker/tx-builder` — Security model

Reference cryptographic infrastructure. This document states the trust boundary, the attacker model,
and every recoverable/fatal error and rejection condition for the security-critical surface of this
package — primarily the **transaction parser** (`parseTxWire`) and the **sighash** (`sighashMessage`).
The goal is to make it easy for an auditor to attack this code and to see why the attack fails.

## Attacker model

Assume a funded adversary who can deliver **arbitrary bytes** to `parseTxWire` (a crafted/oversized/
truncated/non-canonical transaction) and can choose every field of a transaction we are asked to
sign. The adversary's goals we explicitly defend against:

1. **Out-of-bounds read / crash** via a hostile length field (CWE-125/CWE-129/CWE-400).
2. **Value truncation** — making a wallet mis-read an output's satoshi amount (CWE-190).
3. **Malleability** — two distinct byte strings that parse to the "same" transaction, breaking any
   logic that keys on raw bytes or txid (non-canonical CompactSize, trailing bytes).
4. **Parser ambiguity** — one byte string meaning two things (e.g. a segwit-style marker).

## Trust boundary

| | |
|---|---|
| **Trusted inputs** | None by default. The parser trusts only its own code. The builder/sighash trust the caller-supplied `Tx` model (constructed in-process), but treat all *bytes* as hostile. |
| **Untrusted inputs** | Every byte passed to `parseTxWire`, and every count/length those bytes imply. |
| **Recoverable errors** | Any malformed/oversize/truncated/non-canonical input → `{ ok:false, reason, offset }`. The caller MUST fail closed. A failure is **never** a partially-parsed transaction. |
| **Fatal errors** | None on the parse path. No input causes a throw or an OOB read (proven by `INV-TXP-F1`). A non-`Uint8Array` argument is returned as a clean failure, not thrown. |
| **Side effects** | None. Parsing is pure (bytes in, value out) — no I/O, no globals, no logging of input. |
| **State mutation order** | Only the local `ByteReader` cursor advances, and only on a successful read. No external state. |
| **Rollback behaviour** | Not applicable — there is no externally visible state to roll back. A rejected input leaves nothing behind. |
| **Audit evidence** | A failure carries `offset` — the exact byte at which parsing stopped — so a rejected input is reproducible and locatable. |

## Enumerated rejection conditions (`parseTxWire`)

Each is a named, tested negative case (see `INVARIANTS.md`):

- input is not a `Uint8Array` → rejected (`INV-TXP-N1`)
- input larger than `maxBytes` → rejected before parsing (`INV-TXP-N2`)
- input shorter than the 10-byte minimum → rejected (`INV-TXP-N3`)
- truncated `version` (`INV-TXP-N4`)
- truncation at **any** field boundary — prevTxid / vout / scriptSig / sequence / value / script /
  nLockTime (`INV-TXP-N5`, exhaustive over every prefix length)
- trailing bytes after `nLockTime` (`INV-TXP-N6`, malleability)
- non-minimal CompactSize for any count/length (`INV-TXP-N7`, malleability)
- a count exceeding the remaining bytes (`INV-TXP-N8`) — bounds the work, no large allocation
- a script length exceeding the remaining bytes (`INV-TXP-N9`)

## What must never be assumed

- Never use `res.tx` without checking `res.ok`.
- Never treat `satoshis` as a `Number`; it is a `bigint` to stay exact across the full u64 range.
- Never assume `serializeParsedTx(res.tx) === input` unless the input was already canonical — that
  equivalence is exactly what the parser *enforces*, and is the identity invariant under test.
- Never hand the raw `scriptSig`/`script` bytes to anything that has not itself validated them; this
  parser deliberately does not interpret scripts.

## What breaks if these rules are violated

- Dropping minimal-CompactSize or the no-trailing-bytes check reopens **malleability**: an adversary
  re-encodes a transaction to different bytes with the same meaning, breaking byte/txid-keyed logic.
- Reading the value field as a `Number` truncates above 2^53 and can **strand or misdirect funds**.
- Ignoring a `null` from `ByteReader` and substituting a default desynchronises the parser from the
  true byte boundaries — the door back to OOB reads and consensus divergence.

## Non-goals (explicitly out of scope here)

- **Game-rule / consensus validity** (script execution, fees, money supply): not the parser's job —
  the embedded node and the interpreter own that. The parser validates the *envelope*, not the
  *meaning*.
- **Script decoding**: a separate, larger grammar behind the interpreter, kept independent so each
  grammar is auditable on its own and the parser's attack surface stays minimal.
