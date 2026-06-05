# Failure modes

Every boundary's failure behaviour, stated honestly: what is recoverable, what is fatal, what fails
**closed**, and what is still **OPEN**. The rule is fail-closed by default: when in doubt, reject and
do not mutate state.

## Parsers (tx, script, hex, JSON, DER, envelope)

| Input | Behaviour |
|---|---|
| malformed / truncated / oversize / non-canonical | **recoverable** — a discriminated `{ ok:false }` / `null` / typed error; never an uncaught throw, never an OOB read (fuzz-proven). |
| valid | parsed value. |

No parser can crash the caller on hostile input. The caller MUST fail closed on a rejection.

## Relay (`apps/relay-go`)

| Condition | Behaviour |
|---|---|
| publish/subscribe without a valid capability | **fail-closed** 401 (missing) / 403 (invalid/expired/wrong-table/wrong-scope). |
| oversize publish body | 413 (no truncated frame forwarded). |
| publish flood | per-table token-bucket rate limit → 429. |
| slow subscriber | its message is dropped (best-effort speed path) — the client reconciles from the transcript; the relay is never the source of truth. |

## Indexer (`apps/indexer-go`)

| Mode | Condition | Behaviour |
|---|---|---|
| validating | unregistered table / forged sig / unknown seat / class mismatch / non-hex commit-reveal / action without `prev` / equivocation / oversize | **fail-closed** 400 with the specific reason; **no state mutation**. |
| validating | authentic record | stored. |
| opaque (legacy) | any record with txid+tableId | stored as opaque bytes (authenticity is the client's job on replay). |

## Engine / state machine

| Condition | Behaviour |
|---|---|
| illegal or out-of-turn action | rejected by `apply`; cannot corrupt state. |
| absent player — ACTION phase | **accountable drop-and-continue** (audit 3): past the anchored block-height deadline a peer's signed `timeout-claim` applies the engine check-or-fold default for the seat; both honest clients converge (proven, `timeout-claim.test.ts`). |
| absent player — commit/reveal HANDSHAKE phase (multiway) | **accountable drop + re-derive**: a non-responder is dropped at the anchored deadline, the deck is re-derived among the survivors, and the hand continues (the non-responder forfeits its bond on-chain via `bondRevealOrForfeitLocking`). Heads-up (sole opponent vanishes) correctly **fails closed** — a one-player hand cannot form — with funds recovered via the pre-signed refund graph (`fallback.ts`). |

## Randomness / crypto

| Condition | Behaviour |
|---|---|
| no CSPRNG available | **fail-closed**: `cryptoRandomBytes` / the shuffle RNG throw rather than fall back to `Math.random`. |
| commitment/MAC/token comparison | constant-time; a mismatch is simply "not equal" (no early exit, no timing leak). |

## Known environment limitation (not a code gap)

| # | Item | Status |
|---|---|---|
| node-maturity | The on-chain bond FORFEIT branch's maturity is the spending tx's `nLockTime`, enforced by a **production** BSV node (CLTV is a no-op post-Genesis, so it cannot live in-script). The local `bonded-subsat-channel` regtest node does **not** enforce `nLockTime` finality, so `onchain-forfeit-e2e` cannot assert the *premature-rejection* on it (it documents this honestly and proves the rest: REVEAL reclaim, in-script wrong-preimage failure, FORFEIT settlement + value conservation, post-forfeit double-spend rejection). The code is correct and a production node enforces the gate. | documented, not a code gap |

If you find a failure mode that does **not** fail closed, that is a finding — see `AUDIT_GUIDE.md`.
