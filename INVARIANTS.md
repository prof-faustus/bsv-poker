# Invariants (index)

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

System-level invariants and the index of every package/app invariant set. Each invariant is an
**executable claim**: it maps to a positive test, the negative/hostile tests, and (for parsers/
boundaries) a fuzz test. Tests are NAMED for their invariant.

## System-level invariants

| ID | Invariant | Where proven |
|---|---|---|
| SYS-1 | Two honest clients given the same valid actions reach byte-identical state (P2). | `multiplayer-e2e`, `validating-indexer-e2e` |
| SYS-2 | A reconnecting client rebuilds the live state from the transcript exactly. | `reconnect-e2e`, `validating-indexer-e2e` |
| SYS-3 | The relay/indexer are never the source of truth; corrupting them cannot change an outcome. | transport-only design; `THREAT_MODEL.md` |
| SYS-4 | No parser/boundary throws or reads out of bounds on hostile input. | all fuzz suites (TS + Go) |
| SYS-5 | Every external comparison of a secret/commitment/MAC/token is constant-time. | `INV-CT-*`, capability MAC, `lint-security` |
| SYS-6 | No `OP_RETURN` in any script; no `Math.random` in security source. | `lint-opreturn`, `lint-security` |
| SYS-7 | Every committed vector regenerates bit-for-bit; every `REQ-*` traces to a test. | `pnpm reproduce`, `pnpm trace` |
| SYS-8 | Every reference-standard package keeps its `README`/`SECURITY`/`INVARIANTS` trio. | `lint-security` doc-presence gate |

## Package / app invariant sets

| Set | Surface | File |
|---|---|---|
| `INV-READ-*`, `INV-HEX/CT/JSON/DER/RND/SER-*` | hardened primitives + reader | [`packages/protocol-types/INVARIANTS.md`](./packages/protocol-types/INVARIANTS.md) |
| `INV-TXP-*` | transaction parser | [`packages/tx-builder/INVARIANTS.md`](./packages/tx-builder/INVARIANTS.md) |
| `INV-INT-*` | script interpreter + DER signing | [`packages/script-templates-ts/INVARIANTS.md`](./packages/script-templates-ts/INVARIANTS.md) |
| `INV-IXV-*` | validated indexer ingest | [`apps/indexer-go/INVARIANTS.md`](./apps/indexer-go/INVARIANTS.md) |
| capability token claims | relay admission | `apps/relay-go/relay/capability_test.go` |

## Rule for adding an invariant

When you add a security-relevant rule: (1) implement it with its WHY in the code; (2) add the claim to
the nearest `INVARIANTS.md`; (3) add a positive test; (4) add the negative test(s) for what it now
rejects; (5) ensure the fuzz suite still passes. A rule with no negative test is not enforced.
