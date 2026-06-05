# ADR 0005 — The indexer is a convenience projection, not an adjudicator (P3 trust boundary)

Status: **Accepted** (documents a standing design; addresses audit findings #35 and #36, which flag
the indexer for not validating poker legality and for not being the canonical transaction graph).

## Context

Two audit findings read as gaps but are deliberate architecture:

- **#35** — "the indexer does not validate poker legality."
- **#36** — "the indexer stores protocol records/projections; the stated truth is the validated
  transaction graph, not the indexer itself."

Both follow directly from **principle P3** (core §8.1): the relay and indexer are **transport and
index only**, never the source of truth. The indexer's own header says it: *"the indexer is a
CONVENIENCE PROJECTION, never the source of truth. The truth is the validated transaction graph … it
treats Raw as opaque bytes; it never parses game logic."*

## Decision

The indexer **authenticates** records but does **not adjudicate** the game, and it is **not**
authoritative.

- **What the indexer DOES (validating mode, audit 7 — findings #32–34, PASS):** pins each table's
  seat→pubkey map (`RegisterSeats`, no conflicting re-registration), and on ingest checks structure,
  record class, the acting seat's pubkey, the signature, and anti-equivocation. This makes the
  transcript it serves for reconnect/rebuild trustworthy.
- **What the indexer does NOT do (by design):** it does not run a poker engine, does not check action
  legality, and does not decide pot outcomes. `Raw` is opaque to it.

### Where legality actually lives

The **engine** is the sole adjudicator (one core, in TypeScript). Every client applies every action —
its own and each peer's — through `module.apply`, which calls `assertLegal` before mutating state
(audit #24, PASS). An illegal action throws; it is never applied.

**Legality validation OVER the indexer's records (audit #30).** So that the indexer's authenticated
transcript is not merely *authentic* but provably *legal*, a validating layer —
`app-services/src/transcript.ts` `validateHandLegality` — replays the authenticated records through
the SAME canonical engine and returns a verdict, rejecting any illegal action, any forged/extra
(unconsumed) action record, and any reveal that does not match its commit. This adds legality
validation to the indexer path **without** a second poker engine (it uses the one TS engine), so #30
is satisfied with no divergence risk. It is exercised in `transcript-legality.test.ts` and live in
`validating-indexer-e2e` (the real indexer's transcript validates; a spliced over-stack bet is
rejected). Because two honest clients are a
pure function of the same ordered inputs (P2), they converge byte-for-byte, and a peer action is
additionally bound to the agreed prior state (ADR-era audit #12). Legality is therefore enforced
**at every seat, on every action**, and is exercised by the adversarial suite — not delegated to the
indexer.

### Where truth actually lives

1. **The agreed transcript replayed through the engine** — deterministic state (`SDK runHand`/
   `deriveState`, reconnect-e2e rebuilds identical state from the transcript).
2. **The on-chain settlement transaction graph** validated by the node (real BIP-143/FORKID sighash,
   the interpreter, nLockTime finality — the on-chain e2es). The money outcome is whatever the node
   accepts and confirms, not what any indexer says.

The indexer is a cache over (1) for fast reconnect; it can be discarded and rebuilt from the
authenticated record stream without affecting correctness.

## Why not the alternatives

- **Rejected: a second poker engine in Go inside the indexer (to "validate legality").** It would
  duplicate the security-critical core in a second language — the largest class of cross-platform
  divergence bugs and a doubled audit surface — for no gain: legality is already enforced at every
  client (P2), and an indexer that *disagreed* with the engine could not override it without becoming
  the source of truth (violating P3). This is the same "one audited core" reasoning as ADR 0004.
- **Rejected: making the indexer authoritative.** That centralises trust in transport, the exact thing
  P3 forbids; the relay/indexer must remain replaceable and non-load-bearing.

## Consequences

- Findings #35/#36 are intended properties of a P3 system, not defects: the indexer authenticates and
  projects; the engine adjudicates; the node + transcript hold the truth.
- An operator may run the indexer in opaque mode (pure transport) or validating mode (authenticated
  transcript); neither mode changes who decides legality or settlement.
- Tests: indexer authentication in `apps/indexer-go/indexer/indexer_test.go` (+ the live
  `validating-indexer-e2e`); legality enforcement in the engine/adversarial suites; truth via
  `reconnect-e2e` (transcript replay) and the on-chain e2es (settlement graph).
