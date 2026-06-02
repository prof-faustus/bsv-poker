# ADR 0004 — Mode A (reconstruct-at-reveal) signing for Phase 1

**Status:** Accepted (implements core D9 / §4.3)

**Context.** Spending a combined-key `Q_j` UTXO needs a signature under `Q_j`. The spec offers Mode
A (patent-literal: parties disclose per-card scalars at reveal, the closer sums to `w_j` and signs)
and Mode B (threshold/no-reconstruction). Phase 1 must ship without a threshold dependency.

**Decision.** Implement Mode A in `wallet-custody`: per-card scalars are HKDF-derived, **single-game**
(bound to `gid`), and `reconstructAndSign` sums disclosed scalars mod n to sign the close-out. The
software custody backend **refuses** `combineSignShare` (Mode B) so the build cannot present Mode A
while claiming Mode B's "no whole key" property (REQ-CRYPTO-008, P8). The active mode is recorded in
the ruleset and surfaced in the UI.

**Consequences.** Phase 1 has a concrete, patent-faithful signing path with the consequences stated
in ink: single-game keys, bounded hand-window exposure. Mode B (FROST/GG20 via `OB.custody`) is a
Phase-2+ upgrade behind the same `Custody` interface.
