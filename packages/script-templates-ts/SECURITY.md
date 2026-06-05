# `@bsv-poker/script-templates-ts` — Security model

Reference cryptographic infrastructure. This package builds the on-chain locking/unlocking script
templates and contains the **Script interpreter** (`src/interpreter.ts`) — the second hostile-input
grammar of the system (the transaction parser in `tx-builder` returns raw script bytes and defers
their meaning to here). This document states the trust boundary and every recoverable error and
resource bound of that interpreter, and the signing/DER surface.

## Attacker model

Assume a funded adversary who authors **both** scripts in a spend (the unlocking and the locking
script are attacker-influenced on a hostile UTXO) and who wants to:

1. **Crash or hang** a node/client by making the interpreter loop or allocate without bound
   (CWE-400) — e.g. a crafted `OP_CHECKMULTISIG` count, an `OP_DUP` flood, or `OP_MUL` bignum growth.
2. **Forge a spend** — make a script evaluate truthy without the right signature.
3. **Read out of bounds** while we normalise a DER signature (see `signing.ts`, CWE-125/129).

## Trust boundary — interpreter (`evaluate` / `run`)

| | |
|---|---|
| **Trusted inputs** | `ScriptContext.sighashPreimage` (computed by our own sighash code). |
| **Untrusted inputs** | every opcode and pushdata item in the unlocking and locking `Script`. |
| **Recoverable errors** | stack underflow; unbalanced `OP_IF`; unsupported opcode; out-of-range multisig n/m; oversized script number; `OP_RETURN`; failed `VERIFY`/`CHECKSIG`/`CHECKMULTISIG`. All become `{ ok:false, reason }`; the script simply does not authorise the spend. |
| **Fatal errors** | none. No script throws out of `evaluate` (proven by `INV-INT-6` fuzz). |
| **Side effects** | none beyond the local stack. ECDSA verify is pure; no I/O, no globals. |
| **State mutation** | the local stack only; input scripts are never mutated. |
| **Rollback behaviour** | not applicable — pure evaluation, nothing external to roll back. |
| **Audit evidence** | the `reason` string names the exact failing rule. |

## Resource bounds (every loop/value is constant-bounded — NASA P10)

| Bound | Value | Closes |
|---|---|---|
| `MAX_STACK` | 1000 elements | OP_DUP / pushdata flood (memory exhaustion) |
| `MAX_MULTISIG_KEYS` | 20 | the unbounded `OP_CHECKMULTISIG` pop loop — n and m are range-checked **before** any pop |
| `MAX_SCRIPT_NUM_BYTES` | 4096 | unbounded bignum growth via repeated `OP_MUL` (CPU/memory) |

These are validated **before** the work they bound, so the bound is explicit and auditable, never an
incidental "the pop will eventually underflow" terminator.

## DER signature normalisation (`signing.ts` → `readDerEcdsaSig` in protocol-types)

`normalizeLowS` parses a DER ECDSA signature through the bounds-checked `readDerEcdsaSig`, which
validates the frame length and every interior length before indexing (CWE-125/129) and returns the
signature unchanged if it is not a well-formed DER frame — it never reads past the buffer and never
silently reshapes a malformed signature into a different value.

## What must never be assumed

- Never assume a popped value is a small/sane number — it is attacker bytes (hence `num()`'s cap).
- Never assume the stack is non-empty before a pop — use `pop()`, which fails closed.
- Never remove a resource bound "because real scripts are small" — the bound **is** the defence.

## What breaks if these rules are violated

- Removing `MAX_MULTISIG_KEYS` reopens the unbounded-pop DoS (a few-byte script hangs the node).
- Removing `MAX_SCRIPT_NUM_BYTES` lets a handful of `OP_MUL`s allocate gigabytes.
- Removing `MAX_STACK` lets an `OP_DUP` loop exhaust memory.

## Non-goals

- This is the template-subset interpreter, not the full node consensus interpreter (documented as a
  TRACKED ASSUMPTION in `interpreter.ts`). A production swap to the embedded node's interpreter
  re-runs these same template tests unchanged.
