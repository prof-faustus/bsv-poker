# ADR 0005 — Engine auto-advances cooperative reveal/deal phases

**Status:** Accepted

**Context.** §19.E models deal/shuffle/board-reveal as distinct committed states, each an N-of-N
cooperative transition with a timeout-default (core §4.6 M2). In the real protocol each is its own
transaction. The deterministic engine, however, must be a pure function of (ruleset, deck, betting
actions) for replay testing (P2) and the UI must not stall waiting on protocol plumbing.

**Decision.** The game modules drive **betting actions** explicitly and **auto-advance** the
non-betting phases (deal, board reveals, showdown, settle) along the cooperative success path.
The timeout-default for those phases is surfaced via `isTimeoutEligible` (the caller applies it
only after maturity). When everyone is all-in, betting rounds auto-close and streets cascade to
showdown.

**Consequences.** The engine stays a clean pure function and replays deterministically. The
crypto/tx layer (SDK `runHand`) wires the real entropy/shuffle/reveal/settlement transactions
around this engine; the on-chain N-of-N reveal transactions and their recovery branches are added
as the real node is bound (the engine's auto-advance is the cooperative happy path it represents).
