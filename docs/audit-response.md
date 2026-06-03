# Audit response

Point-by-point response to the security audit, with the commit that fixes each finding. Where a fix
is partial, the remaining work is stated honestly (no overstatement — audit 7).

## Critical

| # | Finding | Status | Fix |
|---|---------|--------|-----|
| 1 | Any relay participant can forge another player's action | **Fixed** | Every action envelope is signed by the seat's Ed25519 session key; on receipt the interactive client verifies the signature against the key **registered to the acting seat** and rejects otherwise. The "publish a fold for seat 1" exploit no longer applies. (`session-auth.ts`, `interactive-client.ts`; v3.66) |
| 2 | Commit/reveal can be spoofed for another seat | **Fixed** | Commit & reveal envelopes are signed and verified against the seat's registered key (same path). A reveal is only accepted on the table channel after passing the seat signature check. (v3.66) |
| 3 | Waiting-room seating is Sybil/spoofable | **Mostly fixed** | The join envelope now carries an Ed25519 signature over `join‖tableId‖pub‖nonce`; unsigned/forged joins are rejected, so a `pub` **cannot be spoofed** (you must hold its key). **Remaining:** host/quorum admission and a buy-in proof bound into the seat map (the on-chain buy-in exists via the escrow funding, but is not yet bound into the join). (`lobby.ts`; v3.66) |
| 4 | The "canonical" indexer path is not canonical | **Relabeled; validation pending** | The indexer path is now described as a **signed-envelope replay log**, not a canonical tx graph (the record id is a local content hash, not a protocol txid). Envelopes it stores are now signed (1–2). **Remaining:** make the indexer ingest only validated, branch-bound protocol transactions before any "canonical" claim. (`interactive-client.ts`; v3.68) |

## High

| # | Finding | Status | Fix |
|---|---------|--------|-----|
| 5 | Shuffle modulo bias | **Fixed** | Both the shared `seededShuffle` and the browser `shuffleDeck` use **rejection-sampled** Fisher–Yates over a uint32 source — an exact, unbiased sampler. (`mp-shuffle.ts`, `shuffle.ts`; v3.66) |
| 6 | `parseAndValidate()` not used in the live client | **Fixed** | The interactive client now runs `validateEnvelope()` at every inbound relay boundary **and** verifies the seat signature before accepting. (v3.66) |
| 7 | Web wallet language risks overstatement | **Fixed** | README scope note: "non-custodial BSV" is the Node/on-chain path; the browser client is a **local play-money relay demo** that does not custody/settle real value. (v3.68) |
| 8 | CI does not build the web client | **Fixed** | `tools/ci.ts` now builds the web client (Vite) as a pipeline stage; a broken bundle fails CI. (v3.66) |

## Medium

| # | Finding | Status | Fix |
|---|---------|--------|-----|
| 9 | Relay publishes not rate-limited | **Fixed (rate limit)** | Per-table token-bucket on `POST /tables/{id}/publish` (50/s, burst 100) → `429` when exceeded; bounds trivial floods. **Remaining:** table-scoped capability tokens / signed admission at the relay layer (message signatures already prevent forgery). (`ratelimit.go`; v3.68) |
| 10 | Commit/reveal liveness has no accountable timeout/default | **Partial** | At the **transaction level** this is covered: the pre-signed fallback graph (`fallback.ts`, REQ-TX-008) returns each stake on a withheld reveal, the recovery/two-exit path is proven on-chain (`onchain-recovery-e2e`), and the engine's decision timeout defaults to check-or-fold (never a forced wager). The **live browser** commit/reveal handshake currently fails-closed on timeout (the hand aborts rather than hanging) but does not yet run a signed-deadline forfeiture that drops a non-responder and continues. **Remaining:** a chain/relay-anchored (REQ-TX-007) signed commit/reveal deadline → deterministic drop-and-continue with on-chain bond forfeiture. |

## Remediation order (audit) — current state

1. **Sign every table/join/commit/reveal/action envelope** — done (Ed25519 per seat; 1,2,3,6).
2. **Bind seat assignments to signed public keys and buy-in proofs** — keys: done; buy-in proof binding: remaining (3).
3. **Replace first-seen relay state with validated protocol-transaction replay** — relabeled; validation remaining (4).
4. **Accountable commit/reveal timeout/forfeiture** — tx-level done; live signed-deadline forfeiture remaining (10).
5. **Rejection-sampling shuffle** — done (5).
6. **`parseAndValidate()` on all inbound paths** — done (6).
7. **Relay admission/rate controls** — rate limit done; capability tokens remaining (9).
8. **Web + desktop builds in CI** — web done (8); desktop is a host-local Rust build (lifecycle unit-tested), not in the Rust-less GitHub CI.

**Net:** the action/commit/reveal forgery class (the critical break) is closed and unit-tested; the shuffle is exact; the live client validates + verifies every inbound envelope. The remaining items (buy-in→seat binding, validated-tx canonical indexer, live signed-deadline forfeiture, relay capability tokens) are scoped above and do not reintroduce the forgery vulnerabilities.
