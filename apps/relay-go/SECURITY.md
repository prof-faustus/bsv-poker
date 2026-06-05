# `relay-go` — Security model

The relay is **transport + index only**, NEVER the source of truth (REQ-NET-001). Compromising it
cannot change a game outcome — only deny or garble service, which the client detects (it reconciles
from the canonical transcript). Its security job is **admission control** and **DoS resistance** on the
opaque table fan-out. Source: `relay/`.

## Attacker model

A process (possibly co-resident on loopback, or remote) trying to: read a table's message stream it
was not admitted to; publish into a table to poison/spam it; forge another seat's message; or exhaust
the relay's memory/CPU.

## Defences

| Threat | Defence | Test |
|---|---|---|
| Unauthorised subscribe (read the stream) | a valid table-scoped capability token is REQUIRED; missing → 401, invalid → 403 (checked before the client receives any frame). | `TestSubscribeRequiresCapability` |
| Unauthorised publish (poison the channel) | capability required on publish (verified before rate-limit/body work). | `TestPublishRequiresCapability` |
| Cross-table token reuse | the tableId is inside the HMAC; a token for A is invalid on B. | `TestCapabilityRejectsWrongTable` |
| Scope escalation | a `sub` token cannot publish. | `TestCapabilityRejectsScopeEscalation` |
| Stale token | expiry inside the HMAC, checked against the clock. | `TestCapabilityRejectsExpired` |
| Token forgery | constant-time HMAC compare; foreign-secret tokens rejected; rotating the server secret revokes all tokens. | `TestCapabilityRejectsTampering`, `TestCapabilityRejectsForeignSecret` |
| Gate a private table | optional admission secret; minting requires it (constant-time compare). | `TestMintCapabilityGatedTable` |
| Action forgery | NOT the relay's job — envelope Ed25519 signatures handle it (verified by peers + the indexer). | — |
| Publish flood | per-table token-bucket rate limit (429). | `ratelimit_test.go` |
| Oversize publish | read `maxBody+1`; 413 rather than forwarding a truncated frame. | `relay_test.go` |
| Malformed capability bytes | `verify` never panics on arbitrary token input. | `FuzzCapabilityVerify` (CI, ~1M execs) |

## Trust boundary

- **Trusted:** the per-process capability secret (CSPRNG or `RELAY_SECRET`).
- **Untrusted:** every request, header, body, and token.
- **Recoverable errors:** every auth/parse/limit failure → a specific 4xx; no panic on hostile input.
- **Side effects:** in-memory presence/table state only; the relay holds no game truth.

## CORS allowlist (`RELAY_ALLOWED_ORIGINS`)

CORS is scoped to an **allowlist** (audit #31) — the relay no longer echoes `Access-Control-Allow-Origin: *`.
An allowed Origin is reflected exactly (with `Vary: Origin`); a non-allowed Origin receives no ACAO
header, so the browser blocks the cross-origin response. Non-browser / same-origin callers (no Origin
header — e.g. the Node `RelayClient`, the e2es) are unaffected.

- **Default** (env unset): loopback origins (`http(s)://127.0.0.1|localhost|[::1]`, any port — the
  locally-served/dev web client) plus `https://bsvpoker.local` (the native desktop's WebView2 host).
- **`RELAY_ALLOWED_ORIGINS`** overrides it: a comma list of exact origins, the token `loopback`, or
  `*` to restore the open policy for a public deployment that wants it.

This is defense in depth; the **primary** gate on every mutating route is the table-scoped capability
token, not CORS. Tested in `relay/cors_test.go`.

## Non-goals

Game-rule adjudication is explicitly NOT done here — the relay carries only opaque transport objects
and is never the source of truth (see ADR 0005 for the indexer's matching P3 boundary).
