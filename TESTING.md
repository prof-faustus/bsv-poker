# Testing

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

Tests here are **executable claims**: every security claim in the docs maps to a positive test, the
enumerated negative/hostile tests, and — for any parser or boundary — a fuzz test. A claim with no
negative test is, by our own rule, not considered enforced.

## Taxonomy

| Kind | Where | Purpose |
|---|---|---|
| Unit / property | `packages/*/test/**`, `tests/**` | per-module correctness |
| Adversarial / negative | same, named `*negative*` / `INV-*-N*` | every rejection condition |
| Fuzz (TS) | `safe.test.ts`, `reader.test.ts`, `parse.test.ts`, `interpreter-hostile.test.ts` | no throw / no OOB on hostile input (200k+ iters each) |
| Fuzz (Go) | `relay-go FuzzCapabilityVerify`, `indexer-go FuzzValidateEnvelopeRecord` | no panic on hostile bytes (live in CI, 8s each; ~1M+ execs) |
| Conformance | `adapters/test` | fakes pass the same suite as the real adapters |
| Exhaustive play | `tests/exhaustive-*` | every seat count / variant settles + conserves chips |
| End-to-end (live) | `tools/*-e2e.ts` | real relay/indexer/node, real signed play |

## Claim → test mapping

Each package/app `INVARIANTS.md` is the authoritative claim→test map; the root
[`INVARIANTS.md`](./INVARIANTS.md) indexes them. Tests are NAMED for the invariant they prove
(`INV-TXP-1`, `INV-INT-3`, `INV-IXV-0`, …) so you can jump from a claim straight to its proof.

## Running

```
pnpm ci                 # the gate: typecheck, lints (incl. security), all tests incl. fuzz,
                        # reproduce, traceability, go vet+test, short active Go fuzz
pnpm test               # all node --test suites (includes the TS fuzz suites)
node tools/validating-indexer-e2e.ts     # signed play accepted + forged record rejected (live)
node tools/multiplayer-e2e.ts            # two clients converge byte-for-byte (live)
node tools/onchain-recovery-e2e.ts       # the two on-chain recovery exits (regtest node)
```

Longer active fuzzing on demand:

```
(cd apps/relay-go   && go test ./relay   -run=^$ -fuzz=FuzzCapabilityVerify        -fuzztime=5m)
(cd apps/indexer-go && go test ./indexer -run=^$ -fuzz=FuzzValidateEnvelopeRecord  -fuzztime=5m)
```

## The bar

- Parsers/boundaries: zero uncaught throws and zero OOB on any input (fuzz-proven).
- Negative coverage: every documented rejection condition has a test.
- Determinism: `pnpm reproduce` regenerates every vector bit-for-bit (`REPRODUCIBILITY.md`).
- "It works" means it ran/rendered/passed adversarial tests — never "it builds" or "process alive".
