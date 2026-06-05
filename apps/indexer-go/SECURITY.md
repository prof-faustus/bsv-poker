# `indexer-go` — Security model

Reference cryptographic infrastructure. The indexer serves per-table transcripts that a
**reconnecting client rebuilds state from**. If it stores forged/replayed/corrupt records, that
client rebuilds from poisoned data. This document states the trust boundary and every rejection
condition of the two ingest modes.

## Two explicit modes (never a silent per-table switch)

| Mode | Constructor / flag | Discipline |
|---|---|---|
| **Opaque** (legacy) | `New()` / default binary | Stores records as opaque bytes (REQ-NET-001). The transcript's authenticity is the client's responsibility on replay. |
| **Validating** (audit 7) | `NewValidating()` / `indexer-go -validate` | **Fail-closed.** A record is accepted only for a *registered* table and only if it carries an authentic, well-formed, Ed25519-signed envelope. |

The mode is fixed at process start — there is no implicit fallback. Production runs `-validate`.

## Attacker model (validating mode)

A funded adversary can POST arbitrary records to `/ingest`. Goals defended against:

1. **Forge another seat's envelope** → defeated by Ed25519 verification against the registered
   seat key over the exact canonical message (`INV-IXV-2`, `INV-IXV-3`).
2. **Mislabel a record's class** → defeated by the class↔envelope-type binding (`INV-IXV-5`).
3. **Equivocate** (commit two different values for one (seat,hand)) → defeated by the per-hand pins
   (`INV-IXV-6`).
4. **Replay an action against a different transcript position** → actions must bind a prior state
   hash (`prev`) (`INV-IXV-7`); full state-hash continuity is checked by the client engine on replay.
5. **Poison an unregistered table** → all ingest is rejected until the seating is registered
   (`INV-IXV-4`).
6. **Crash the indexer with malformed Raw** → `parseEnvelope`/`validateEnvelopeRecord` never panic
   (`INV-IXV-F1` fuzz).

## Trust boundary

| | |
|---|---|
| **Trusted inputs** | the registered seat→pubkey map (the lobby's signed seating agreement, fixed once via `RegisterSeats`). |
| **Untrusted inputs** | every `/ingest` record and every byte of its `Raw` envelope. |
| **Recoverable errors** | not-registered, malformed envelope, class mismatch, unknown seat, bad signature, non-hex commit/reveal, action without `prev`, equivocation, oversize envelope → 400 with the specific reason. |
| **Fatal errors** | none. No record bytes cause a panic. |
| **Side effects** | on success only: the record is appended and the equivocation pins updated. **A rejected record mutates nothing** (pins are written only after all checks pass). |
| **State mutation order** | dedup check → append to order → store record → update pins; all under the indexer lock. |
| **Rollback behaviour** | not needed — a rejected record never begins mutation. |
| **Audit evidence** | the 400 reason names the exact failed check; the txid locates the record. |

## The canonical-message interop contract (critical)

The Go validator reconstructs the signed message as
`JSON.stringify([tableId, t, seat, hand, kind??'', amount??0, c??'', r??'', discard??[], prev??''])`
— **byte-for-byte** what the TypeScript client signs (`session-auth.ts` `envelopeMessage`). HTML
escaping is disabled and the trailing newline trimmed so the bytes match exactly. This equivalence is
pinned to a literal expected string in `TestCanonicalMessageInteropVector` (`INV-IXV-0`); if it ever
drifts, real client signatures stop verifying — so the test fails loudly.

## Non-goals

- **Game legality / settlement validity** is NOT checked here. Re-implementing the poker engine in Go
  would create a second engine that could diverge from the canonical one — a consensus split worse
  than no check. The client replays the *authenticated* transcript through the single canonical
  engine (`transcript.ts`). The indexer validates the game-rule-agnostic layer: authenticity,
  structure, binding, non-equivocation. This split is the deliberate security boundary.
