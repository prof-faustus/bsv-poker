# Red-Team Review 02 — the *implementation* (`bsv-poker`)

**Scope.** Red-Team Review 01 (`spec/bsv-poker-spec-redteam-01.md`) audited the *specification*. This
pass audits the **built system** on `master`: does the implementation actually close RT-01's
findings, and what new risks does the running code introduce? Every claim below cites the satisfying
source + a **passing test** (run via `node tools/ci.ts`: 151 TS + 16 Go) or a host-local E2E.

**Method.** Adversarial behaviours are exercised in `packages/sdk/test/adversarial.test.ts` and the
on-chain / dependency E2Es (`tools/onchain-*-e2e.ts`, `tools/va-bind-e2e.ts`, `tools/ob-bind-e2e.ts`).
Negative cases must fail **inside the real Script interpreter**, not in a wrapper.

---

## Disposition of RT-01 findings

### B1 — "combined private key never reconstructed in one place" vs the GB2616862 mechanism
**Status: MITIGATED (Mode A); Mode B key-setup REAL, online signing OUTSTANDING.**
- Mode A (`reconstruct-at-reveal`) does transiently reconstruct the per-hand scalar:
  `packages/wallet-custody/src/custody.ts` `reconstructAndSign` — covered by
  `packages/wallet-custody/test/custody.test.ts` ("Mode A reconstructAndSign sums scalars and
  produces a valid signature"). The build is **honest about this**: software custody **refuses**
  `combineSignShare`, so it cannot falsely claim Mode B (same test file).
- The Mode B mitigation (no party ever holds the whole key) now has a **real** key source: the
  `overlay-broadcast` threshold custody generates the t-of-n group key
  (`packages/adapters/src/real-ob.ts`, `tools/ob-bind-e2e.ts` — 2/3, 3/5, 6/9 on-curve group keys;
  no whole-key reconstruction).
- **Residual risk:** the *online* t-of-n signing protocol that emits a single signature under the
  group key is not yet wired (OB does not expose signing via its CLI). Until then, settlement that
  must avoid any reconstruction relies on the **N-of-N CHECKMULTISIG** funding path (each player
  signs with their own key, never shared) — proven on-chain in `tools/onchain-poker-e2e.ts`. This is
  a security-equivalent avoidance of reconstruction for the pot, but not the single-signature Mode B.

### B2 — "selection = spending the UTXO" vs dealing to positions
**Status: RESOLVED.** Dealing is an explicit position→card map, not "spend = deal":
`packages/app-services/src/mp-shuffle.ts` (`deckFromEntropies` composes each player's secret
permutation) feeds the per-variant position deal in the game modules; `packages/sdk/test/table.test.ts`
("runHand wires entropy/shuffle/deal/betting/settlement into one hand") exercises the full mapping,
and card-substitution at reveal **fails inside the interpreter**
(`packages/sdk/test/adversarial.test.ts`, "card-substitution at reveal fails INSIDE the interpreter").

### M1 — determinism overstated before confirmation
**Status: RESOLVED, honestly.** Post-confirmation determinism: transcript replays to **byte-identical**
state (`table.test.ts` "deriveState replays the transcript to byte-identical state"). Pre-confirmation
non-determinism is handled by the **nSequence original-replacement rule**, demonstrated on-chain
(`tools/onchain-recovery-e2e.ts`: a higher-sequence cooperative spend supersedes the broadcast
timeout-default). No claim of pre-confirmation finality is made.

### M2 — board reveals are N-of-N cooperative ops with a liveness failure mode
**Status: RESOLVED.** Liveness is guaranteed by the **pre-signed fallback graph**
(`packages/tx-builder/src/fallback.ts`, `presignFallbackGraph`) + the decision/recovery timeout
layers; `packages/tx-builder/test/fallback.test.ts` verifies the pre-signed refund validates inside
the interpreter and is value-conserving, and `adversarial.test.ts` ("timeout-default applied keeps
the hand progressing (no freeze, P4)") confirms no freeze. The two-exit mechanism is real on-chain
(M1 evidence).

### M3 — fair-play scaling to 52 cards assumed, not measured
**Status: MITIGATED.** The in-script EC fair-play proof is implemented and **byte-measured**
(`packages/script-templates-ts/src/templates.ts` `fairPlayEcLocking`, with wire-byte vectors in
`packages/script-templates-ts/test/templates.test.ts`; ~231 B/card). Hot-path cost is now measured
(`tools/perf-suite.ts`). **Residual:** a full 52-card deck end-to-end fair-play timing/cost profile is
not yet published as a single vector.

### M4 — fakes can hide protocol-security bugs; bind fakes to real implementations
**Status: MOSTLY RESOLVED.** Security-critical behaviours now run against **real** implementations:
real CT crypto (`packages/crypto-mentalpoker`), real BSV node (`@bsv-poker/adapters/real-node`,
`tools/onchain-*-e2e.ts`), real VA Merkle (`@bsv-poker/adapters/real-va`, `tools/va-bind-e2e.ts`),
real OB threshold custody (`@bsv-poker/adapters/real-ob`, `tools/ob-bind-e2e.ts`). The *single*
conformance suite now runs identically against the fake **and the real VA adapter**
(`tools/conformance-real-e2e.ts` runs `runVAConformance(realVAContract())` — same suite, real
`@vaa/merkle`). **Residual (REQ-DEP-003):** OB's contract methods (`isRevoked(height)`,
`thresholdSplit` of a supplied secret) are not exposed by its CLI, so OB conformance-vs-real is not
yet wired; CT (`cardtable`) is absent on disk.

### M5 — Power-of-Ten literal compliance not achievable in a GC'd runtime
**Status: RESOLVED, honestly.** `docs/adr/0003-*` records the non-literal adaptation; the
**measurable** part (bounded working memory in the hot path) is now actually measured:
`tools/perf-suite.ts` runs 200k hot-path evals under `--expose-gc` and asserts ~0 retained heap
(REQ-APP-092). No false claim of literal NPR-7150.2 compliance.

### M6 — reveal-token + signing were both `DECISION REQUIRED` (two holes)
**Status: ONE CLOSED, ONE PARTIAL.** Reveal: commit-reveal binds the value
(`adversarial.test.ts` "withheld/incorrect entropy reveal is detected by the commitment"). Signing:
Mode A is implemented + tested; Mode B online signing is the remaining hole (see B1 residual).

### m1–m5 (MINOR)
- **m1 (participant set per hand):** RESOLVED — lobby + waiting room define the set
  (`packages/ui-core/src/view-models/network-lobby.ts`, seat-range tests in `table-room-vm.test.ts`).
- **m2 (on-chain cost model):** PARTIAL — per-tx wire bytes are measurable (real serialization in
  `packages/tx-builder/src/wire.ts`); a published fee/cost model is outstanding.
- **m3 (odd-chip rule):** RESOLVED in the engine pot logic (`packages/engine/src/pots.ts`,
  `pots.test.ts`).
- **m4 (OP_RETURN vs pushdata / shuffle-stage commitments):** RESOLVED — OP_RETURN is banned in
  live scripts and rejected by the interpreter; `tools/lint-opreturn.ts` enforces it
  (REQ-TX-010). Dead-end shuffle-stage commitments may still use it (REQ-TX-009).
- **m5 (glossary "trustless" hygiene):** documentation-only; reflected in `docs/user-guide.md`.

---

## New findings (from auditing the running system)

### F1 — Mode B online threshold signing is unimplemented (carries B1/M6 residual). **MAJOR.**
The single-signature, no-reconstruction settlement path is not yet wired (OB exposes key-gen +
revocation, not online signing). *Mitigation in place:* N-of-N CHECKMULTISIG settlement (no shared
key) proven on-chain. *Action:* bind OB's signing protocol (or an audited threshold-ECDSA library).

### F2 — Conformance-against-real is partial (REQ-DEP-003). **MAJOR → reduced to MINOR.**
The identical conformance suite now passes against the **real VA** adapter
(`tools/conformance-real-e2e.ts`). Remaining: **OB** (its CLI doesn't expose the contract's
`isRevoked(height)` / `thresholdSplit(secret)` — needs the OB library/daemon, not the CLI) and
**CT** (`cardtable` absent on disk). *Action:* bind OB's library for `thresholdSplit`/revocation and
gate CT when the repo is present.

### F3 — Mainnet safety relies on the regtest default, not a hard gate. **MAJOR (pre-mainnet).**
The node defaults to regtest and the build is research-only, but there is no signed-binary chain of
trust (installers are unsigned) and no explicit, tested mainnet-enable flag with a confirmation gate
(REQ-PROD-012 is traced for the banner; the *flag* path needs a test). *Action:* code-sign the
MSI/NSIS, add a tested `--network=main` gate behind explicit acknowledgement before any mainnet use.

### F4 — Replacement-rule reliance assumes miner policy. **MINOR.**
On-chain recovery (M1/M2) depends on the node honouring the original nSequence replacement rule. The
embedded node does; a mainnet miner's policy may differ. *Action:* document the assumption and add a
pre-signed-graph fallback that does not depend on replacement (already partly covered by F-graph).

---

## Verdict
RT-01's two BLOCKERS are **resolved (B2) and mitigated with an honest residual (B1)**; the MAJORs are
resolved or mitigated with named residuals. The system does not ship a security claim it cannot back
with a passing test. **The remaining security-relevant work is F1 (Mode B online signing), F2
(conformance-vs-real for VA/OB + CT), and F3 (mainnet hardening + code-signing)** — none of which
block research/regtest play, all of which gate a mainnet deployment.
