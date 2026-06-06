# Threat model

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

This is **open reference cryptographic infrastructure**: the goal of this document is to make it easy
for an attacker or auditor to find a flaw, by stating every assumption, asset, trust boundary, and
defended threat — each with the test that proves the defence. If a claim here has no test, treat it
as unproven. Where a defence is not yet implemented, it is marked **OPEN**.

## Assets (what an adversary wants)

| Asset | Why it matters |
|---|---|
| Players' staked funds / bonds | Direct theft or stranding. |
| The agreed game state (the transcript) | Forking it lets a cheater claim a different outcome. |
| Hidden card faces before reveal | Seeing opponents' cards breaks the game. |
| Deck order (shuffle) | A predictable/biased shuffle is an edge. |
| Seat identity / session keys | Impersonating a seat forges actions. |
| Liveness of a table | A stalled table denies service. |

## Adversary model

A **funded, active** adversary who can: run a modified client; deliver arbitrary bytes to every
network boundary (relay, indexer, node RPC) and to every parser (tx, script, hex, JSON, DER); choose
every field of any transaction we are asked to sign; observe timing; and co-reside with the loopback
services. We do **not** assume the adversary controls the embedded BSV node's consensus, the player's
local CSPRNG, or the player's private keys.

## Trust boundaries

```
  human ─┐
         ▼
  [ web/desktop UI ]  ──(menu actions only; no AI decisions)──►  [ app-services ]
         │                                                              │
         │  untrusted relay channel (Ed25519-signed envelopes)          │  deterministic engine
         ▼                                                              ▼   (pure; no I/O)
  [ relay-go ]  ◄── capability tokens, fail-closed ──►  [ peers ]   [ game-* / hand-eval ]
         │                                                              │
         ▼  untrusted transcript                                        ▼  on-chain bytes
  [ indexer-go ]  ── validating ingest (Ed25519) ──►              [ tx-builder + node ]
```

Trusted: the local engine code, the local CSPRNG, the embedded node's validation, the player's keys,
and (in the indexer's validating mode) the registered seat→pubkey map. **Everything else is hostile.**

## Threats and defences (by surface)

| # | Threat | Surface | Defence | Proof |
|---|---|---|---|---|
| T1 | OOB read / crash via a hostile length field | tx parser, script | bounds-checked `ByteReader`; `parseTxWire` bounds every length; interpreter bounds multisig/stack/bignum | `INV-TXP-*`, `INV-READ-*`, `INV-INT-*` (+450k/100k fuzz) |
| T2 | Value truncation (mis-count an output) | tx parser | satoshis carried as bigint | `INV-TXP-2` |
| T3 | Transaction malleability | tx parser | minimal CompactSize + no trailing bytes | `INV-TXP-N6/N7` |
| T4 | Action forgery (impersonate a seat) | relay protocol | every envelope Ed25519-signed; receivers verify against the registered seat key | `interactive-client` signed path; indexer `INV-IXV-2/3` |
| T5 | Unauthorised channel access / spam | relay | table-scoped capability tokens, fail-closed (401/403); per-table rate limit + 413 | `capability_test.go`, `INV-IXV`/relay e2e |
| T6 | Poisoned transcript → bad reconnect | indexer | validating ingest authenticates every envelope; equivocation rejected | `INV-IXV-0..9` (+ live e2e) |
| T7 | Timing oracle on a commitment/MAC/token | crypto compares | constant-time comparison everywhere | `INV-CT-1/2`, capability constant-time MAC |
| T8 | Predictable shuffle / nonce / id | randomness | CSPRNG-only, fail-closed; `Math.random` banned in CI | `INV-RND-*`, `lint-security` |
| T9 | Biased shuffle (modulo bias) | mental poker | rejection sampling, bounded | `realct` perm test; `CARD_AND_DECK_MODEL.md` |
| T10 | Resource exhaustion via huge JSON / unframed stream | network boundaries | `safeJsonParse` (size/depth/nodes); bounded SSE/socket buffers | `INV-JSON-*` |
| T11 | Late-entropy shuffle manipulation | mental poker | commit-then-reveal; order = composition of secret perms | `spec §4.1`; commit/reveal tests |
| T12 | Grindable seat assignment | lobby | beacon order = H(all signed-join nonces); CSPRNG nonces | `lobby.ts`; audit-02 #9 |
| T13 | Funds stranded by an absent player | on-chain | pre-signed timeout-default refund graph (replaceable by cooperative settlement) | `fallback.ts`, `onchain-recovery-e2e` |
| T14 | **Accountable** drop + bond forfeiture of a non-responder | live + on-chain | anchored block-height deadline + signed `timeout-claim` drop-and-continue (action AND handshake phases) + on-chain `bondRevealOrForfeitLocking` | `timeout-claim.test.ts` (convergence + premature/forged rejection), `INV-BOND-1..5`, `onchain-forfeit-e2e` |

## Explicit non-goals

- The browser web client is a **play-money local demo** (localStorage balance); it does not custody
  or settle real value (README scope note).
- The platform interpreter is the **template subset**, not the full node consensus interpreter
  (a TRACKED ASSUMPTION; production swaps to the embedded node's interpreter).
- The indexer does **not** validate game legality/settlement (that is the client engine's job — see
  `DESIGN_DECISIONS.md`: "no second engine in Go").

## How to attack this (auditor entry points)

Start at `AUDIT_GUIDE.md`. The highest-value targets are the two hostile-input grammars
(`tx-builder/parse.ts`, `script-templates-ts/interpreter.ts`) and the two authentication boundaries
(`relay-go/capability.go`, `indexer-go/validate.go`). Each has a `SECURITY.md` + `INVARIANTS.md`.
