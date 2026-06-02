# BUILD STATUS — honest metrics (core §17, app §A20.2)

> Completeness is never claimed beyond what is written and tested. Every number here is
> produced by running the build (`pnpm ci`), not asserted. Last full run: CI **GREEN**.

## Pipeline (`pnpm ci`)

| Stage | Result |
|---|---|
| `tsc --strict` (all strict flags, whole workspace) | green |
| OP_RETURN lint (0x6a absence, rule 2) | green |
| TS tests (`node --test`) | **110 pass / 0 fail** |
| Go tests (relay + indexer) | **16 pass / 0 fail** |
| `reproduce` (every vector regenerates bit-for-bit) | green |
| traceability | **72 / 223 requirements traced; all Phase-0/1 gate reqs → passing tests** |
| web client `vite build` | green → `apps/client-web/dist` (62 modules, 56.8 kB gzip) |
| `node tools/selftest.ts` (stack up + full hand E2E) | PASS |

## Phases

- **Phase 0 — Foundations — GATE MET** ✅ (tag `v0.0.0-phase0`): monorepo, adapters+conformance
  fakes, protocol-types + §19.A serialization, reproduce + traceability, VM self-test, CI green.
- **Phase 1 — First playable (heads-up NL Hold'em) — core MET; shells partial** :
  - ✅ entropy commit/reveal, distributed shuffle, encrypted-card deal, full preflop→river
    betting FSM, showdown, **settlement spend verified through the real interpreter**, fold
    without reveal, decision/recovery timeout defaults, transcript + **deterministic replay**
    (SDK `runHand`/`deriveState`); interpreter tests green for every template used.
  - ✅ **running web client** (`vite build` → playable hot-seat-vs-bot Hold'em on the real engine;
    lobby, table, legality-driven bet sizer, signing modal, consequence text, regtest banner).
  - ✅ **Windows desktop app BUILT** (Tauri): `bsv-poker-desktop.exe` + installers
    `bsv-poker_0.1.0_x64_en-US.msi` and `bsv-poker_0.1.0_x64-setup.exe`; the Rust supervisor
    implements the §A3.2 lifecycle + IPC.
  - ✅ relay/indexer **multi-client networking** (RelayClient/IndexerClient; the self-test
    exercises discovery + dual-path + the deterministic projection over the live Go services).
  - ✅ **real embedded BSV node bound** (D6): `pnpm node-e2e` starts the real
    bonded-subsat-channel regtest node and mines blocks through the platform adapter (height 0→2).
- **Phase 2 — Robustness — partially delivered**: fair-play template (mismatch forfeits inside
  the interpreter) + §19.C measured byte schedule + per-card/per-batch decision; multi-way N-seat
  Hold'em with side-pot settlement + button rotation; **9-case adversarial suite** (§14.6).
- **Phase 3 — Variants — MET (modules)**: Omaha, Seven-Card Stud, Five-Card Draw, Razz game
  modules, each tested; hand-eval reproduces the §19.D vectors. (TODO: Omaha-8 hi-lo split path
  exists in hand-eval, gated off in the module; authentic FL bring-in completion sizing.)
- **Phase 4 — seams**: VA audit (boundary surfaced, no truth-at-origin) + OB revocation
  (unspent-expiring-output) integration-tested against the fakes.

## Packages (TS) + apps (Go/Rust)

protocol-types · hand-eval · engine · game-holdem · game-omaha · game-stud · game-draw ·
game-razz · crypto-mentalpoker · adapters · script-templates-ts · tx-builder · wallet-custody ·
sdk · ui-core · app-services · tools · relay-go · indexer-go · client-web (Vite, builds) ·
client-desktop (Tauri, **builds to MSI + NSIS installers**).

## Non-negotiable rules — enforced, not aspirational

BSV-only/post-Genesis (CLTV/CSV no-ops, tested) · **OP_RETURN banned** (serialize throws + lint +
interpreter reject) · zero fabrication (`reproduce`) · real-interpreter negative tests (fail
inside) · complete traceability for every claimed requirement · RT-01 B1/B2 not reintroduced.

## What remains (honest)

1. **Full crypto/tx hand on the real node**: drive a complete Hold'em hand's funding/deal/reveal/
   settlement transactions through the bound bonded-subsat-channel node (node mining is bound;
   the per-tx submit path is next), and bind the real CT/cardtable + VA + OB repos, re-running the
   conformance suite against them.
2. **Multi-client live play**: connect two browser clients over the relay (the networking
   primitives + self-test exist; the browser-to-browser sync UI is the next pass).
3. **Mode B** threshold signing (`OB.custody`); full pre-signed fallback graph; minimum-reveal
   showdown crypto; Omaha-8 hi-lo split.
4. **Traceability**: 148 / 223 requirements remain `planned (later phase)` — see
   `spec/traceability.txt`.
