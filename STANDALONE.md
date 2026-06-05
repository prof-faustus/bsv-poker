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
asserted), **`onchain-live-forfeit`** (audit #22 — a live `InteractiveNetworkedTableClient` table
drops a non-revealer AND forfeits its bond on-chain via the `ForfeitureCoordinator`, all in one flow),
`onchain-recovery`, `onchain-spend`, `onchain-poker`, `onchain-table`, `wallet`, `onchain`, `node`,
`onchain-live`, `bot-onchain` (single + two-process), `settlement-service`, `microbet`.

## View layer is framework-free and bundler-free (no React / Vite / Tauri)

The browser client and the shared view components are now **100% framework-free**: there is **no
React, no Vite, no bundler** anywhere in the tree.

- **Web client** (`apps/client-web`, `ui-core/src/{dom,components}.ts`): rendered by the in-tree
  ~90-line DOM toolkit `dom.ts` (`el`/`mount`/`text`) over the standard browser DOM API — no virtual
  DOM, no reconciliation, no framework. Text is set via `textContent` only (never `innerHTML`), so
  the view layer cannot inject markup (no XSS) — proved by `verify-dom.ts` (19 assertions in a real
  headless browser, incl. the negative XSS case and `mount` focus/caret preservation).
- **In-tree build** (`apps/client-web/build.ts`): `tsc` type-strip + a generated ES-module **import
  map** — the browser's own module loader is the runtime. No Rollup/esbuild/Vite. The built client is
  load-verified in headless Chrome/Edge (`verify-render.ts`) every CI run.
- **Desktop client** (`apps/client-desktop/native`): a **true native Win32 application in C**
  (Windows SDK + Microsoft Edge **WebView2**) — **no Tauri, no Rust, no framework**. It hosts the same
  audited web client (one core, not a fork) and supervises the Go services under a kill-on-close Job
  Object. Its render is proved headlessly (`verify-desktop.ts` reads `#root` text out of the live
  WebView2 DOM); its lifecycle policy is unit-tested (`native/test-lifecycle.c`).

## The one boundary stated plainly

- **Build/language tooling**: the TypeScript compiler (`typescript`) and Node type stubs
  (`@types/node`). These are the language toolchain, not an external system; the runtime is Node's
  standard library.
- **OS components used by the native desktop host** (Windows only): the Win32 API and the Microsoft
  **WebView2** runtime — operating-system components, the same category as `windows.h`. The WebView2
  ABI header + static loader are vendored in-tree (`apps/client-desktop/native/{include,lib}`,
  provenance in `native/THIRD-PARTY.md`) so the host builds offline. The shipped exe carries no extra
  DLL (the loader is linked statically).

No part of the security model depends on any UI framework or browser — the real value path is the
Node-side SDK, which is dependency-free, and both clients render the same dependency-free core.
