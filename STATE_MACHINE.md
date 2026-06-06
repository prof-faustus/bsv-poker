# State machine

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

The game engine is a **pure** finite-state machine: a function of its inputs with no I/O, no time, no
randomness (P2 / REQ-ARCH-002). This is what makes replay and cross-client agreement possible. Source:
`packages/engine/src/fsm.ts` and the per-variant `packages/game-*`.

## The `GameModule` contract (`fsm.ts`)

```
init(ruleset, seats)            -> state
getLegalActions(state, seat)    -> LegalActions     (the legal-move descriptor)
apply(state, action)            -> state'           (pure transition)
isTimeoutEligible(state, now)   -> { seat, defaultAction } | null   (the safe default move)
isHandComplete(state)           -> boolean
settle(state)                   -> Payouts
serialize(state)                -> bytes            (the canonical state bytes -> stateHash)
```

`enumerateActions` turns a `LegalActions` descriptor into the literal `Action[]`; `replay(module,
ruleset, seats, actions)` re-derives state from an ordered action list — the deterministic-core
driver used by reconnect/rebuild and dispute replay.

## Transition rules (per action)

For each betting action `fold` / `check` / `call` / `bet` / `raise` (and `discard` in Draw):

- **Precondition:** the seat is the one on the clock (`betting.toAct` or `drawToAct`); the action is
  in `getLegalActions(state, seat)`. An action that is not legal is rejected by `apply` — it cannot
  corrupt state.
- **Valid actor:** exactly the seat named by `toAct`. A signed envelope from another seat is rejected
  at the trust boundary before it reaches `apply` (`PROTOCOL.md`).
- **State mutation:** pure; produces a new state. The prior state hash is bound into the action's
  signature (`prev`) so the move is pinned to one transcript position.
- **On-chain:** a settlement/fallback transaction is produced at terminal states (`ONCHAIN_MODEL.md`).
- **Rejection:** an illegal or out-of-turn action is a no-op rejection; the transcript/indexer never
  stores an unauthenticated or illegal move (validating mode).

## Timeout default — accountable drop-and-continue (audit 3, IMPLEMENTED)

`isTimeoutEligible(state, now)` returns the **safe default** for the seat on the clock (check-or-fold).
The deterministic *application* of that default when a peer is absent is the accountable drop, and it
is implemented in `interactive-client.ts` for **both** phases:

- **Action phase:** past the anchored block-height deadline (`floor + window`), a peer's signed
  `timeout-claim` applies the engine check-or-fold default for the seat as a replayable branch.
- **Handshake phase (multiway):** a non-responder to commit/reveal is dropped at the anchored
  deadline and the deck is **re-derived among the survivors** (a withheld permutation is an absent
  input, not a move with a default — so the survivors exclude it and continue). The two-phase
  commit-before-reveal ordering is preserved, so late-entropy protection (core §4.1) is intact.

Safety rests on a **shared anchored deadline** — the block height both clients observe — so they drop
at the identical logical point and never fork (`tools`... `timeout-claim.test.ts` proves byte-for-byte
convergence and rejects premature/forged claims). The dropped player forfeits its bond on-chain
(`bondRevealOrForfeitLocking`, `onchain-forfeit-e2e`). Heads-up where the sole opponent vanishes
correctly fails closed (a one-player hand cannot form); funds recover via the pre-signed refund graph.

## Convergence guarantee (P2)

Two honest clients applying the same valid action set reach **byte-identical** state — proven live by
`tools/multiplayer-e2e.ts` and `tools/validating-indexer-e2e.ts` (both players' `stateHash` match) and
by the reconnect/rebuild path (`tools/reconnect-e2e.ts`: rebuilt-from-transcript state == live state).
This is why the engine forbids I/O, time, and randomness: any of those would break determinism.
