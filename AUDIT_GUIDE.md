# Audit guide

This codebase is written to help you attack it. This guide is the shortest path from "I want to break
it" to the exact file, the exact invariant, and the exact test.

## Reading order

1. [`THREAT_MODEL.md`](./THREAT_MODEL.md) — assets, adversary, trust boundaries, threat→defence→test.
2. [`ARCHITECTURE.md`](./ARCHITECTURE.md) — the package/app map and data flow.
3. The two **hostile-input grammars** (highest value):
   - `packages/tx-builder/src/parse.ts` — the raw transaction parser (the worked reference).
   - `packages/script-templates-ts/src/interpreter.ts` — the Script interpreter.
4. The two **authentication boundaries**:
   - `apps/relay-go/relay/capability.go` — table-scoped capability tokens.
   - `apps/indexer-go/indexer/validate.go` — validated envelope ingest.
5. The **primitive layer** everything rests on: `packages/protocol-types/src/{safe,reader,serialize}.ts`.

Every file above carries a full WHAT/HOW/WHY/security-boundary header, and every package/app above
has a `SECURITY.md` (boundary, attacker model, rejection conditions) and an `INVARIANTS.md` (each
claim → its positive/negative/fuzz test).

## How the claims map to tests

Each invariant has an ID (`INV-TXP-*`, `INV-INT-*`, `INV-IXV-*`, `INV-READ-*`, `INV-HEX/CT/JSON/DER/
RND-*`) and a test NAMED for it. Open the package `INVARIANTS.md`, pick a claim, and jump to the
test. A claim with no negative test is, by our own rule, not considered enforced — if you find one,
that is a finding.

## Things worth trying to break

- Feed `parseTxWire` a transaction with a non-minimal CompactSize or trailing bytes (should reject).
- Feed the interpreter a script with a huge `OP_CHECKMULTISIG` count (should reject fast, not hang).
- Mint a relay capability for table A and use it on table B (should 403).
- Post a forged or equivocating envelope to a `-validate` indexer (should 400).
- Find any hex/JSON/DER input that throws an *uncaught* exception (the fuzz tests claim none exists).
- Find a `===` comparison on a secret/commitment/MAC, or a `Math.random` on a security value (CI
  bans the latter; the former should all be `constantTimeEqual*`).
- Find a parsed length used to index before it is bounds-checked.

## Running everything

```
pnpm ci                      # typecheck, lints, all tests incl. fuzz, reproduce, traceability, go vet+test+fuzz
node --test "packages/**/test/**/*.test.ts" "tests/**/*.test.ts"
(cd apps/relay-go && go test ./... && go test ./relay -run=^$ -fuzz=FuzzCapabilityVerify -fuzztime=60s)
(cd apps/indexer-go && go test ./... && go test ./indexer -run=^$ -fuzz=FuzzValidateEnvelopeRecord -fuzztime=60s)
node tools/validating-indexer-e2e.ts    # live signed play accepted + forged record rejected
```

## The honest state (be fair, look here)

[`FAILURE_MODES.md`](./FAILURE_MODES.md) and [`docs/audit-response-03.md`](./docs/audit-response-03.md)
state it plainly. All three originally-deferred audit findings (3, 5, 7) are now closed and verified.
The one remaining **environment** limitation (not a code gap): the local regtest node does not enforce
`nLockTime` finality, so `onchain-forfeit-e2e` documents — rather than asserts — the premature-rejection
maturity gate (a production-node guarantee), while proving the rest on the real node.
