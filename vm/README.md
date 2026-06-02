# The self-contained runtime ("the VM") — core §10 / D5

A reproducible stack that launches **node(regtest) + relay + indexer + client** with no
external services (REQ-VM-001/002/003). Regtest by default; mainnet only behind an explicit
research flag (REQ-VM-007).

## One-command bootstrap + self-test (no Docker required)

```
node tools/selftest.ts      # or: pnpm selftest
```

This builds the Go services, starts the relay (`:8091`) and indexer (`:8092`), waits for
`/healthz`, runs a full heads-up Hold'em hand through the engine (the client role), prints the
transcript + final state hash + payouts, and tears the services down. This is the Phase-0 gate
check: "VM launches end-to-end, self-test passes" (core §17).

## Container packaging (REQ-VM-003)

```
docker compose -f vm/docker-compose.yml up --build
```

- `node-regtest` (:18332) — Phase-0 placeholder; the real **bonded-subsat-channel** embedded
  node binds next (D6, §10.2).
- `relay` (:8091) — transport + indexing only, never source of truth (core §8.1).
- `indexer` (:8092) — per-table tx projections.
- `client` (:5173) — Phase-0 placeholder; `apps/client-web` (Vite) lands in Phase 1.

Builds are reproducible (pinned toolchains, `-trimpath`, distroless static images;
REQ-VM-006). A literal hypervisor image (OVA/qcow2) is an optional extra artifact from the
same composition (D5, REQ-VM-005) — not built by default.
