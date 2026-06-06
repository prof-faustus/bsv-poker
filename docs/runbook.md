# Runbook (operations)

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](../P2P_MODEL.md).

Per app spec §A15. How to build, test, run, and verify the platform.

## Prerequisites
- Node ≥ 24 (native TypeScript type-stripping; `node --test`).
- pnpm 9. Go ≥ 1.24. Desktop host: MSVC Build Tools (`cl.exe`) + WebView2 runtime — native Win32, no Tauri (ADR 0004).
- Installs on a TLS-inspecting host: prefix with `NODE_OPTIONS="--use-system-ca"`.

## Install
```
NODE_OPTIONS="--use-system-ca" pnpm install
```

## The commands (root package.json)
| Command | Purpose |
|---|---|
| `pnpm typecheck` | `tsc --strict --noEmit` across the workspace |
| `pnpm test` | all `node --test` suites |
| `pnpm reproduce` | regenerate every vector; non-zero on mismatch (core §14.5) |
| `pnpm lint:opreturn` | fail if OP_RETURN (0x6a) appears in any script |
| `pnpm trace` | requirements → code → test traceability |
| `pnpm requirements` | regenerate `spec/requirements.yaml` from the specs |
| `pnpm selftest` | bring the stack up + run a full hand E2E (the Phase-0 gate check) |
| `pnpm ci` | the full pipeline (typecheck → lint → tests → reproduce → trace → go test) |

## Run the stack (self-test, no Docker)
```
node tools/selftest.ts
```
Builds the Go services, starts relay (:8091) + indexer (:8092), waits for `/healthz`, runs a full
heads-up hand, prints the transcript + state hash + payouts, tears down.

## Run the container stack
```
docker compose -f vm/docker-compose.yml up --build
```
node-regtest (:18332, placeholder pending the bonded-subsat-channel bind) · relay (:8091) ·
indexer (:8092) · client (:5173).

## Run the web client (dev)
```
NODE_OPTIONS="--use-system-ca" pnpm --filter @bsv-poker/client-web dev      # dev server
NODE_OPTIONS="--use-system-ca" pnpm --filter @bsv-poker/client-web build    # static bundle → dist
```

## Data directories & network
- Regtest by default (REQ-VM-007); mainnet only behind the explicit research flag, with an
  unmissable UI banner. Data dirs are namespaced by network so regtest/mainnet never share state.
- The desktop supervisor binds local services to loopback by default (§A10.7).

## Recovery
- A red CI stage blocks merge — fix the failing stage (the runner stops at the first failure).
- `reproduce` mismatch ⇒ a vector drifted; investigate the change, then `node tools/reproduce.ts
  --write` only if the change is intentional, and commit the new vector with justification.
