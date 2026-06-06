# Failure modes

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

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

## On-chain maturity gate — enforced in-tree

The bond FORFEIT branch's maturity is the spending tx's `nLockTime`. The project's **own** in-tree node
(`@bsv-poker/adapters/regtest-node`) enforces `nLockTime` finality (`IsFinalTx`): a non-final
transaction (any input `nSequence != 0xffffffff`) with a future locktime is **rejected** until its
locktime is reached. `tools/onchain-forfeit-e2e.ts` asserts this for real — a premature FORFEIT claim
is rejected, and it confirms only at maturity (`INV-NODE-2` proves the gate in isolation). No external
node, no disclaimer.

If you find a failure mode that does **not** fail closed, that is a finding — see `AUDIT_GUIDE.md`.
