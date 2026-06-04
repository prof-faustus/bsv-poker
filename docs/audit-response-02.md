# Audit response — second pass

Point-by-point response to the second audit, with the commit per finding. Partial fixes state the
remaining work honestly (no overstatement).

## Critical

| # | Finding | Status | Fix |
|---|---------|--------|-----|
| 1 | Envelope auth still optional in the live client | **Fixed** | `InteractiveNetworkedTableClient` now **throws** unless `auth` + `seatPubs` are supplied; unsigned mode is only reachable behind an explicit `allowUnsigned` test flag. Inbound verification is mandatory for live play. (v3.71) |
| 2 | Lobby join signatures conditional | **Fixed** | `joinWaitingRoom` **throws** without a signing key; unsigned joins only behind the `allowUnsigned` test flag. Every accepted join proves possession of `pub`. (v3.71) |
| 3 | No accountable commit/reveal timeout/default | **Partial** | Tx-level recovery is done and proven on-chain: the pre-signed fallback graph returns each stake (`fallback.ts`, REQ-TX-008) and the two-exit recovery confirms on the node (`onchain-recovery-e2e`); the engine decision-timeout defaults to check-or-fold. The live handshake fails-closed on timeout (the hand aborts) but does not yet run a **signed-deadline forfeiture** that drops a non-responder and continues with a replayable default branch. **Remaining:** chain/relay-anchored (REQ-TX-007) signed commit/reveal deadlines → deterministic drop-and-continue + on-chain bond forfeiture. |

## High

| # | Finding | Status | Fix |
|---|---------|--------|-----|
| 4 | Modulo bias in `crypto-mentalpoker/realct.ts` | **Fixed** | `permutationFromEntropy` is now rejection-sampled (the last biased shuffle) — same exact sampler as `app-services/mp-shuffle`. (v3.71) |
| 5 | Relay authentication absent | **Partial** | Per-table token-bucket rate limit (v3.68) **and** fail-closed `413` on oversize bodies (v3.71, finding 6). **Remaining:** table-scoped capability tokens / signed admission at the relay layer (envelope signatures already prevent action forgery; this is anti-spam / anti-poisoning). |
| 6 | Publish body truncates rather than rejects | **Fixed** | `handlePublish` reads `maxBody+1` and returns `413 Payload Too Large` if the extra byte exists — no truncated frames forwarded. (v3.71) |
| 7 | Indexer is an opaque projection, not a verifier | **Acknowledged; remaining** | Already relabeled as a signed-envelope replay log (not canonical). **Remaining (production):** ingest only validated protocol transactions (real txid, signatures, branch binding, state hash, successor commitment, hand/table binding, legal transition, settlement validity). |

## Medium

| # | Finding | Status | Fix |
|---|---------|--------|-----|
| 8 | Signature doesn't bind prior transcript/state hash | **Fixed** | `envelopeMessage` now binds `prev` (the prior state hash) into the signed action; the client sets `prev = stateHash(state-before-action)`. A signed action can't be replayed against a different transcript position (unit-tested). (v3.72) |
| 9 | Deterministic seating manipulable by key choice | **Fixed (hardened)** | Seat order is now a **beacon order**: beacon = H(all signed-join nonces), seat order = sort by H(beacon‖pub). A player can't grind a pubkey for a favourable seat because the order key passes through a hash bound to every player's committed nonce. (Full non-grindability against an adaptive last-joiner still benefits from nonce pre-commit/reveal — a further step.) (v3.72) |
| 10 | CI skips web build if Vite absent | **Fixed** | The Vite build stage no longer skips on missing Vite — it fails unless the explicit `BSV_CI_SKIP_WEB=1` dev flag is set. (v3.71) |

## Remediation order — current state

1. Mandatory signed session auth for live play — **done** (1).
2. Mandatory signed lobby joins — **done** (2).
3. Accountable commit/reveal timeout + forfeiture — **tx-level done; live signed-deadline remaining** (3).
4. Fix modulo bias in `realct.ts` — **done** (4).
5. Relay admission/capability + fail-closed body — **413 + rate limit done; capability tokens remaining** (5,6).
6. Indexer: validated-transaction verification — **remaining** (7).
7. Bind signed actions to prior transcript/state hash — **done** (8).
8. Non-grindable seat assignment — **done (beacon); pre-commit/reveal a further step** (9).
9. Web build non-skippable in CI — **done** (10).

**Net:** 7 of 10 findings fixed (1,2,4,6,8,9,10); 5 partial (rate-limit + 413 done, capability tokens remaining); 3 and 7 are the remaining protocol/verification tracks (signed-deadline forfeiture; validated-tx indexer), both scoped above. The optional security paths are now mandatory; the remaining items are additive hardening + production transaction verification, and do not reintroduce the fixed forgery/seating/shuffle defects.
