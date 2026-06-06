# Security policy

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

This repository is **open reference cryptographic infrastructure** intended for hostile review,
reuse, attack, and extension. Security is treated as co-equal with functionality: every
security-relevant module documents WHAT/HOW/WHY and its security boundary, and every security claim
maps to a positive, negative, and (for parsers) fuzz test.

## Reporting a vulnerability

Open a GitHub issue describing the surface, the input, and the observed vs expected behaviour. If you
have a crashing input for a parser or a forgery for a boundary, include it — these are the bugs we
most want. There is no production deployment holding real user funds (the web client is a play-money
demo; the on-chain path is regtest by default), so disclosure can be public.

## Security model (overview)

| Layer | Boundary doc | Invariants |
|---|---|---|
| Primitives (hex, constant-time, JSON, DER, reader) | [`packages/protocol-types/SECURITY.md`](./packages/protocol-types/SECURITY.md) | [INVARIANTS](./packages/protocol-types/INVARIANTS.md) |
| Transaction parsing/sighash | [`packages/tx-builder/SECURITY.md`](./packages/tx-builder/SECURITY.md) | [INVARIANTS](./packages/tx-builder/INVARIANTS.md) |
| Script interpreter/templates | [`packages/script-templates-ts/SECURITY.md`](./packages/script-templates-ts/SECURITY.md) | [INVARIANTS](./packages/script-templates-ts/INVARIANTS.md) |
| Relay admission | (capability tokens) | [`apps/relay-go`](./apps/relay-go) `capability.go` + tests |
| Indexer authentication | [`apps/indexer-go/SECURITY.md`](./apps/indexer-go/SECURITY.md) | [INVARIANTS](./apps/indexer-go/INVARIANTS.md) |

The full adversary model, asset list, trust boundaries, and threat→defence→test table are in
[`THREAT_MODEL.md`](./THREAT_MODEL.md). The auditor's reading order is in
[`AUDIT_GUIDE.md`](./AUDIT_GUIDE.md).

## Enforced security rules (CI)

- **No `Math.random` in source** (CWE-338) — `tools/lint-security.ts` fails the build.
- **No `OP_RETURN` in any script** (core P11) — `tools/lint-opreturn.ts`.
- **Strict, never-throwing parsers** — proven by fuzz tests run every CI run (TS) plus active Go
  fuzzing of the capability and indexer boundaries.
- **Doc-presence gate** — a reference-standard package cannot lose its `README`/`SECURITY`/
  `INVARIANTS` trio (`tools/lint-security.ts`).
- **Determinism** — `pnpm reproduce` regenerates every committed vector; non-zero on mismatch.
- **Traceability** — every `REQ-*` maps to code and a passing test (`pnpm trace`).

## Cryptographic dependencies

- secp256k1 ECDSA + ECDH, SHA-256, RIPEMD-160, HKDF-SHA256, HMAC-SHA256 via Node `crypto`.
- Ed25519 (session signatures) via Web Crypto (browser-safe).
- Portable pure-TS SHA-256 for the deterministic core (identical output in Node and browser).

No bespoke cryptographic primitives are implemented; constructions (commit/reveal, combined keys,
capability tokens, key derivation) compose standard primitives and are documented in
[`CARD_AND_DECK_MODEL.md`](./CARD_AND_DECK_MODEL.md), [`KEY_LIFECYCLE.md`](./KEY_LIFECYCLE.md), and
[`ONCHAIN_MODEL.md`](./ONCHAIN_MODEL.md).
