# `@bsv-poker/script-templates-ts` — Invariants (executable claims)

Every security claim about the Script interpreter and the signing surface, mapped to the test that
proves it. Run:

```
node --test "packages/script-templates-ts/test/**/*.test.ts"
```

## Interpreter resource bounds (hostile input) — `interpreter-hostile.test.ts`

| ID | Claim | Proof (test name) |
|---|---|---|
| INV-INT-1 | `OP_CHECKMULTISIG` with a huge pubkey count is rejected **before** any pop loop (no hang). | `INV-INT-1 negative: OP_CHECKMULTISIG huge pubkey count rejected fast` |
| INV-INT-2 | A pubkey count one over the consensus cap (21) is rejected. | `INV-INT-2 negative: OP_CHECKMULTISIG pubkey count 21 rejected` |
| INV-INT-3 | A stack-depth flood is rejected (no OOM). | `INV-INT-3 negative: stack depth limit enforced` |
| INV-INT-4 | An oversized script-number operand is rejected, not arithmetic'd. | `INV-INT-4 negative: oversized script number rejected` |
| INV-INT-5 | Stack underflow is a clean `{ ok:false }`, never a throw. | `INV-INT-5 negative: underflow is a clean failure` |
| INV-INT-6 | **No** random script throws out of `evaluate` (100k fuzz). | `INV-INT-6 fuzz: 100k random scripts never throw` |

## Interpreter correctness (legitimate scripts) — `templates.test.ts`, `shuffle-key.test.ts`

| ID | Claim | Proof |
|---|---|---|
| INV-INT-7 | A correct signature satisfies the fold/funding/reveal/settlement templates; a wrong key/preimage fails **inside** the interpreter (P9), not in a wrapper. | `templates.test.ts` (fold pass/fail, 2-of-2 funding pass + under-signed fail, reveal pass + bad-preimage fail) |
| INV-INT-8 | The in-script EC fair-play proof verifies a committed on-curve shuffle key and rejects a cheat INSIDE the interpreter (256-bit arithmetic). | `shuffle-key.test.ts` |

## Bond reveal-or-forfeit (audit-3 on-chain half) — `templates.test.ts`

| ID | Claim | Proof (test: `bond reveal-or-forfeit: …`) |
|---|---|---|
| INV-BOND-1 | The bond owner reclaims by revealing the committed preimage + signing (reveal branch). | positive: owner reclaim accepted |
| INV-BOND-2 | The pot beneficiary claims the forfeited bond via the timeout branch (maturity is tx-level). | positive: beneficiary forfeit-claim accepted |
| INV-BOND-3 | A wrong preimage fails the reveal branch INSIDE the interpreter (P9). | negative: bad preimage rejected |
| INV-BOND-4 | The owner cannot take the forfeit branch (it pays the beneficiary key). | negative: owner-on-forfeit rejected |
| INV-BOND-5 | The beneficiary cannot take the reveal branch without the preimage (commitment binds it). | negative: beneficiary-on-reveal rejected |

## DER signature normalisation — covered in `protocol-types/test/safe.test.ts`

| ID | Claim | Proof |
|---|---|---|
| INV-INT-9 | `readDerEcdsaSig` never reads out of bounds on any buffer; `normalizeLowS` leaves a malformed DER frame unchanged rather than reshaping it (CWE-125/129). | `safe.test.ts`: `readDerEcdsaSig parses…and rejects malformed`, `FUZZ: readDerEcdsaSig never reads out of bounds (100k)` |

## How to extend

A new opcode or template adds: the implementation with its WHY, a positive test (a legitimate script
that uses it), the negative test(s) for the inputs it now rejects, and — if it introduces any
attacker-controlled loop or value — a resource bound plus the hostile test that proves the bound.
