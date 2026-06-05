# `@bsv-poker/script-templates-ts`

The on-chain **Script templates** (funding, reveal-or-timeout, fold, settlement), a real secp256k1
**signing** surface, and the **Script interpreter** that executes them with Genesis rules.

This package contains the project's second hostile-input grammar — the **interpreter**
([`src/interpreter.ts`](./src/interpreter.ts)). The transaction parser in `@bsv-poker/tx-builder`
returns raw script bytes and defers their meaning to here, so the interpreter is held to the same
bar: every attacker-controlled loop and value is explicitly bounded, and no script can throw out of
`evaluate`.

## WHAT

| Module | What |
|---|---|
| `interpreter.ts` | `evaluate(unlocking, locking, ctx)` — a bounded, never-throwing Script stack machine (Genesis rules; real ECDSA `OP_CHECKSIG`/`OP_CHECKMULTISIG`). |
| `templates.ts` | The protocol locking/unlocking script builders, each binding the anti-replay `BranchBinding`. |
| `signing.ts` | secp256k1 keygen and LOW-S DER signing (`normalizeLowS` via the bounds-checked `readDerEcdsaSig`). |
| `script.ts` / `opcodes.ts` | The `Script`/`ScriptItem` model, serialization, and the opcode table. |

## HOW the interpreter stays safe

One forward pass over the script items; IF/ELSE/ENDIF tracked on an execution-flag stack; any
underflow/decode error caught locally and returned as `{ ok:false, reason }`. Resource bounds —
`MAX_STACK` (1000), `MAX_MULTISIG_KEYS` (20), `MAX_SCRIPT_NUM_BYTES` (4096) — are validated *before*
the work they bound. See the file header and [`SECURITY.md`](./SECURITY.md) for the full rationale.

## WHY

A spend's scripts are attacker-authored. An interpreter that can be made to loop or allocate without
bound is a denial-of-service primitive; one that can be made to evaluate truthy without the right
signature is a theft primitive. The interpreter is written so both are impossible by construction and
so the negative cases fail INSIDE the machine (the P9 obligation), not in a guard around it.

## Security & invariants

- [`SECURITY.md`](./SECURITY.md) — attacker model, trust boundary, resource bounds, non-goals.
- [`INVARIANTS.md`](./INVARIANTS.md) — every claim mapped to its positive/negative/fuzz test.

## Tests

```
node --test "packages/script-templates-ts/test/**/*.test.ts"
```

`interpreter-hostile.test.ts` proves the resource bounds (incl. a 100k-iteration fuzz that no script
throws); `templates.test.ts` and `shuffle-key.test.ts` prove the legitimate scripts — including the
256-bit in-script EC fair-play proof — pass and that cheats fail inside the interpreter.
