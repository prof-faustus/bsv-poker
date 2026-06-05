# Audit response 04 — 40-point review

Row-by-row response to the 40-item audit. Each row: the current status and the concrete evidence
(file / test / ADR). Items marked **FIXED THIS ROUND** changed in response to this audit; the commits
are on `master`. Nothing here is aspirational — every claim cites a passing test or a live e2e.

| # | Item | Status | Evidence |
|--:|------|--------|----------|
| 1 | Overall result | **Addressed** | All actionable findings below fixed or documented; CI green (376 tests + Go + build + render). |
| 2 | BSV node as infrastructure | PASS | In-tree `RegtestNode` (no external process); README/STANDALONE.md. |
| 3 | Relay/server as transport | PASS | P3; `apps/relay-go`, `network-gate.ts`. |
| 4 | Browser is play-money/demo | PASS | `app.ts` wallet is play-money localStorage; `web-interaction-rules.test.ts`. |
| 5 | Live session auth mandatory | PASS | `interactive-client` constructor rejects live play without `auth`+`seatPubs`. |
| 6 | Unsigned test mode isolated | **HARDENED THIS ROUND** | `allowUnsigned` enabling is now BANNED in shipped src by the security lint (gate proven by a planted violation); available only to tools/ + test/. |
| 7 | Seat envelopes signed | PASS | `publish()` signs every envelope; `session-auth.ts`. |
| 8 | Inbound structural validation | PASS | `safeJsonParse` → `validateEnvelope` before use; `message-validation.ts`. |
| 9 | Inbound signature verification | PASS | `subscribe()` verifies sig vs `seatPubs[seat]`. |
| 10 | Sig binds table/hand/seat/payload | PASS | `envelopeMessage()` positional binding incl. `prev`,`d`,`h`,`subject`. |
| 11 | Action publishes prior-state hash | PASS | local action includes `prev = stateHash` before publish. |
| 12 | **Peer action `prev` checked before apply** | **FIXED THIS ROUND** | `interactive-client.playOneHand` binds peer actions to the agreed state (apply / stale-skip / forged-abort); `peer-prev-binding.test.ts` (positive + negative). |
| 13 | Shared commit phase before reveal | PASS | `collectHandshake` PHASE 1 commits-only. |
| 14 | Shared reveal after all commits | PASS | `collectHandshake` PHASE 2. |
| 15 | Deck from all active parties | PASS | `deckFromEntropies(active)`. |
| 16 | One party cannot fix the deck | PASS (for protocol) | deck = composition of active seats' secret permutations; a dropped non-revealer is excluded, never able to set the deck alone. |
| 17 | Safe if one machine's memory is exposed | PARTIAL (inherent) | one party's exposure reveals only THAT party's own cards/decisions — unavoidable for any client; others' entropy stays secret (per-hand, KEY-LIFECYCLE.md). |
| 18 | Deck shuffle unbiased | PASS | `seededShuffle` rejection sampling. |
| 19 | Permutation unbiased | PASS | `permutationFromEntropy` rejection sampling. |
| 20 | Multi-party permutation composition | PASS | `shuffledDeck` composes each party's secret permutation. |
| 21 | Commit/reveal timeout path | PASS | anchored-deadline signed `timeout-claim`; `timeout-claim.test.ts`. |
| 22 | **Timeout forfeiture on-chain in the live path** | **FIXED THIS ROUND** | `onSeatDropped` event → `ForfeitureCoordinator` forfeits the non-revealer's bond at maturity; `forfeiture-coordinator.test.ts`, `seat-drop.test.ts`, `onchain-live-forfeit-e2e.ts`. |
| 23 | Action timeout/default path | PASS | engine check-or-fold default applied on drop. |
| 24 | Peer actions rule-checked by engine | PASS | `module.apply` calls `assertLegal` before mutation; ADR 0005. |
| 25 | Lobby joins require signatures | PASS | `joinWaitingRoom` rejects missing `sign` unless test flag. |
| 26 | Join possession proof verified | PASS | Ed25519 over the (now commitment-binding) join message. |
| 27 | **Seat ordering non-grindable** | **FIXED THIS ROUND** | commit-reveal seating — nonces fixed before any disclosure; `seat-ordering.test.ts`, SEATING.md. Sybil = economic (buy-in per seat), stated plainly in SEATING.md. |
| 28 | Relay publish capability-gated | PASS | relay requires a table capability before work. |
| 29 | Relay subscribe capability-gated | PASS | same. |
| 30 | Relay body oversize fail-closed | PASS | reads `limit+1` → 413. |
| 31 | Relay CORS | **HARDENED THIS ROUND** | CORS scoped to an allowlist (default: loopback + WebView2 host; `*`/explicit via `RELAY_ALLOWED_ORIGINS`) — no longer echoes `*`; `cors_test.go` (allowed reflected, external denied). Capability tokens remain the primary gate. |
| 32 | Indexer validating mode | PASS | `NewValidating()` requires registered tables + signed envelopes. |
| 33 | Indexer pins table keys | PASS | `RegisterSeats` refuses conflicting re-registration. |
| 34 | Indexer authenticates records | PASS | structure/class/seat-key/signature/anti-equivocation. |
| 35 | **Indexer poker legality** | **BY DESIGN (documented)** | P3 — the indexer authenticates, the ENGINE adjudicates (assertLegal + P2 replay); ADR 0005. |
| 36 | **Indexer canonical tx graph** | **BY DESIGN (documented)** | truth = engine replay + on-chain tx graph; the indexer is a discardable projection (P3); ADR 0005. |
| 37 | **Tx builder is full production BSV** | **CLARIFIED** | real BIP-143/FORKID `wire.ts::sighashMessage` is the on-chain artifact (settlement/fold/recovery/bonded-channel/node), proven byte-for-byte vs bitcoinx (`wire.test.ts`); the SDK's simplified `sighashPreimage` is orchestration-only (comment corrected). |
| 38 | OP_RETURN avoided | PASS | pushdata commitments; `lint-opreturn.ts` + interpreter rejects 0x6a. |
| 39 | Per-hand fresh entropy | PASS | `H(table_entropy ‖ handIndex)`; `key-lifecycle.test.ts`. |
| 40 | **One-game key lifecycle manifest** | **FIXED THIS ROUND** | KEY-LIFECYCLE.md + `key-lifecycle.test.ts` (fresh key per session root, domain separation, distinct per-hand entropy). |

## Summary of changes made in response to this audit

- **#12** — peer-action prior-state binding (stale-vs-forged distinction), `interactive-client.ts`.
- **#22** — on-chain bond forfeiture wired into the live drop path (`onSeatDropped` →
  `ForfeitureCoordinator`), proven end-to-end on the in-tree node.
- **#27** — commit-reveal (non-grindable) seating in `lobby.ts`.
- **#40** — explicit key & entropy lifecycle manifest.
- **#37** — corrected misleading doc; the real BIP-143/FORKID sighash was already the on-chain path.
- **#35/#36** — ADR 0005 makes the indexer's P3 trust boundary an explicit, citable design decision.

The remaining PARTIALs are inherent or economic, and stated as such: **#17** (a player's own machine
compromise is unavoidable for any client) and **#27-Sybil** (a buy-in, not an identity oracle, is the
admission control on play-money regtest tables — SEATING.md).

## Addendum — deeper hardening on re-audit

A follow-up review pressed three items to a stricter bar; addressed:

- **Transaction builder full-production sighash.** The SDK's cooperative close-out (`sdk/table.ts`
  `verifySettlement`) previously signed a SIMPLIFIED in-process preimage; it now signs the REAL
  BIP-143 (FORKID) `wire.ts::sighashMessage(tx, 0, fundingLocking, potSats)` — the same production
  digest the on-chain settlement/fold/recovery and the node use. No orchestration path signs a
  non-production preimage anymore.
- **Key lifecycle as code.** The one-game key manifest is now a discoverable CODE artifact —
  `packages/app-services/src/key-lifecycle.ts` exports the real `perHandEntropy(...)` derivation the
  live client uses (the inline copy removed — single source) plus a machine-readable `KEY_LIFECYCLE`
  manifest. `key-lifecycle.test.ts` cross-checks the derivation byte-for-byte and validates the manifest.
- **Forfeiture as a named client-path driver.** `ForfeitureCoordinator.settle()` is the shipped driver
  a node client calls to drive a reveal non-responder's bond to actual on-chain forfeiture;
  `onchain-live-forfeit-e2e` drives it through the REAL `InteractiveNetworkedTableClient.onSeatDropped`
  event. The browser client is custody-free and only emits the event — forfeiture is a Node-side
  capability by design (like the indexer's P3 boundary), not browser glue.

Genuinely-by-design / out-of-phase (not code fixes): the **indexer is not the canonical tx graph and
does not adjudicate legality** (P3 — ADR 0005; legality is the engine's, truth is the engine replay +
the on-chain graph), and **real-value production readiness** is out of scope for a Phase-1 regtest
play-money system (mainnet/custody-HSM hardening + external audit is a separate program, not a patch).
