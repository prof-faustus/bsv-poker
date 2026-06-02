# relay-go

Phase-1 hosted **relay** for bsv-poker (BSV-only, post-Genesis).

> **REQ-NET-001 (core §8.1, P3):** the relay is **transport + indexing only and
> is NEVER the source of truth**. It accelerates convergence and fans out
> opaque table messages; it never parses, validates, or adjudicates game logic.
> The truth is the validated transaction graph, reconstructed identically by
> each client (P2). A lying/faulty relay is detectable by the client because the
> client rebuilds state from the canonical tx set (REQ-NET-007).

Zero external dependencies — Go standard library only (`net/http`, SSE).

## Run

```
go run . -addr 127.0.0.1:8091
```

Flags:
- `-addr` loopback listen address (default `127.0.0.1:8091`; `8081` is taken on the build host).
- `-presence-ttl` heartbeat expiry window (default `30s`).
- `-sweep-interval` presence expiry sweep cadence (default `10s`).

## Two-tier model (core §8.2, REQ-NET-002)

### Tier A — discovery (presence + table directory)

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/presence` | Register/refresh presence. Body: `{"playerId","addr"}` (heartbeat/keepalive). |
| `DELETE` | `/presence/{id}` | Explicit leave. |
| `GET` | `/presence` | List live presence (id-sorted). Stale entries expire by TTL. |
| `POST` | `/tables` | Create a table. Body: `{"id","name"}`. `409` on duplicate id. |
| `GET` | `/tables` | List tables (id-sorted) with subscriber counts. |

### Tier B — table-scoped opaque object relay (Bitmessage-style)

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/tables/{id}/publish` | Fan out the raw request body (opaque bytes, ≤1 MiB) to all subscribers. Returns `{"delivered":N}`. |
| `GET` | `/tables/{id}/subscribe` | Subscribe via Server-Sent Events; each opaque object arrives as one `data:` event. |

The relay **stores and forwards opaque bytes**; it does not interpret them. Slow
subscribers drop messages on the speed path (bounded per-subscriber buffer) and
reconcile via the canonical tx graph — the relay never blocks or invents state.

### Health

| Method | Path | Response |
|---|---|---|
| `GET` | `/healthz` | `200` + `{"status":"ok"}` (supervisor liveness, app §A3.2). |

## Tests

```
go test ./...
```

Covers presence register/expiry/heartbeat-refresh, table create/join/list,
Tier-B publish→subscribe fan-out to all subscribers, publish buffer-copy
isolation, and `/healthz`.
