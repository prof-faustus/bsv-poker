# Standalone — no external systems

This project is **standalone**: every security-critical part builds and runs on in-tree code and the
language runtime alone. It relies on **no external system, no separate project, and no external
process** at runtime.

## Verified: zero external dependencies in the security-critical core

| Layer | Package(s) | External runtime deps |
|---|---|---|
| Hardened primitives + parsers | `protocol-types`, `tx-builder` | **none** |
| Crypto / scripts / sighash | `crypto-mentalpoker`, `script-templates-ts`, `wallet-custody` | **none** |
| Engine + games + eval | `engine`, `game-*`, `hand-eval` | **none** |
| App services + SDK | `app-services`, `sdk`, `adapters` | **none** |
| **In-tree BSV node** | `adapters/regtest-node` + `tools/regtest-node-daemon.ts` | **none** (Node stdlib) |
| **In-tree bonded channel** | `adapters/bonded-channel` | **none** |
| Relay + indexer | `apps/relay-go`, `apps/indexer-go` | **none** (Go stdlib — `go.mod` has zero `require`s) |

Every one of these packages declares only `workspace:*` dependencies (other in-tree packages). The
Go services import only the standard library.

## What was removed to get here

- The external **`bonded-subsat-channel` python node** (a separate project) — replaced by the in-tree
  `RegtestNode` (runs the real interpreter; enforces **nLockTime finality**, sequence replacement,
  double-spend, value conservation) and its TCP daemon for multi-process e2es.
- The external **bonded micro-payment channel** — replaced by the in-tree `BondedChannel`.
- The live **python sighash cross-check** — replaced by committed bitcoinx vectors (independent
  reference frozen as data; `tools/sighash-interop.ts`).
- All hardcoded external paths, the external node adapter `real-channel.ts`, and the `_sighash_ref.py`
  helper.

A repo-wide sweep confirms **no external process is spawned anywhere** in `tools/` or `packages/*/src/`.

## On-chain layer runs entirely in-tree

Every on-chain end-to-end test runs against the in-tree node (in-process, or via the in-tree daemon
for the multi-process cases) and passes: `onchain-forfeit` (incl. the nLockTime maturity gate
asserted), `onchain-recovery`, `onchain-spend`, `onchain-poker`, `onchain-table`, `wallet`, `onchain`,
`node`, `onchain-live`, `bot-onchain` (single + two-process), `settlement-service`, `microbet`.

## The one boundary stated plainly

- **Build/language tooling**: the TypeScript compiler (`typescript`) and Node type stubs
  (`@types/node`). These are the language toolchain, not an external system; the runtime is Node's
  standard library.
- **View layer (NOT security-critical)**: the browser **play-money demo** (`apps/client-web`) and the
  shared view components (`ui-core`) currently use React, and the desktop shell uses the Tauri CLI.
  The security model does **not** depend on any of these — the real value path is the Node-side SDK,
  which is dependency-free. Replacing the UI framework with an in-tree, framework-free implementation
  (and the desktop shell with a native Windows app) is a separate, view-layer-only workstream.
