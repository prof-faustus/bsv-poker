# `indexer-go` — Invariants (executable claims)

Every security claim about validating ingest, mapped to its test. Run:

```
go test ./...
go test ./indexer/ -run=^$ -fuzz=FuzzValidateEnvelopeRecord -fuzztime=30s
```

## Validating ingest — `indexer/validate_test.go`

| ID | Claim | Proof (test) |
|---|---|---|
| INV-IXV-0 | The Go canonical signed message is **byte-identical** to the TypeScript client's `JSON.stringify` (else real signatures won't verify). Pinned to a literal vector. | `TestCanonicalMessageInteropVector` |
| INV-IXV-1 | A genuine signed envelope on a registered table is accepted. | `TestValidatingIngestAcceptsAuthentic` |
| INV-IXV-2 | A forged signature (different key) is rejected. | `TestValidatingIngestRejectsForgedSig` |
| INV-IXV-3 | A record for an unregistered seat is rejected. | `TestValidatingIngestRejectsUnknownSeat` |
| INV-IXV-4 | An unregistered table rejects all ingest (fail-closed). | `TestValidatingIngestRejectsUnregisteredTable` |
| INV-IXV-5 | A class tag that disagrees with the envelope type is rejected. | `TestValidatingIngestRejectsClassMismatch` |
| INV-IXV-6 | Commit equivocation (two values for one (seat,hand)) is rejected and does not mutate the prior record. | `TestValidatingIngestRejectsEquivocation` |
| INV-IXV-7 | An action without a prior-state binding (`prev`) is rejected. | `TestValidatingIngestRejectsActionWithoutPrev` |
| INV-IXV-8 | Opaque mode is unchanged (back-compat) — accepts a bare opaque record. | `TestOpaqueModeStillAccepts` |
| INV-IXV-9 | A seat map is pinned: identical re-registration is idempotent; a conflicting one is refused. | `TestRegisterSeatsPinning` |
| INV-IXV-F1 | `validateEnvelopeRecord` never panics on arbitrary `Raw` bytes (655k+ fuzz execs). | `FuzzValidateEnvelopeRecord` |

## Live end-to-end — `tools/validating-indexer-e2e.ts`

| ID | Claim | Proof |
|---|---|---|
| INV-IXV-E1 | Real signed interactive play is accepted by the `-validate` indexer (transcript non-empty, rebuilds to the same state) AND a forged record posted to `/ingest` is rejected (400). | `node tools/validating-indexer-e2e.ts` |

## How to extend

A new validation rule adds: the check in `validateEnvelopeRecord` (with its WHY), a row here, a
positive test, the negative test for the inputs it now rejects, and — if it touches `Raw` parsing —
confirmation the fuzz target still passes. A rule with no negative test is not considered enforced.
