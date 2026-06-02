# ADR 0002 — Portable pure-TypeScript SHA-256 in protocol-types

**Status:** Accepted

**Context.** `protocol-types` (canonical serialization, `rulesetHash`, state hashing) is imported
by the engine, hand-eval, and the game modules, all of which must run **in the browser** (core
§3.2/§11.1, one core two shells). `node:crypto` is unavailable in a browser bundle, and Web
Crypto's SHA-256 is async — but the serializer's hashing is synchronous and deterministic.

**Decision.** Implement SHA-256 as pure TypeScript in `protocol-types/src/sha256.ts` and use it for
`sha256`/`hash256`. It produces byte-identical output to `node:crypto` (verified: `reproduce`
vectors are unchanged). `node:crypto` remains for Node-only code: the Script interpreter (ECDSA)
and the custody backend.

**Consequences.** The deterministic core is environment-agnostic and the web client imports it
directly. The pure-TS hash is slower than native, but it is not on a tight hot path (state hashing
is per-transition, not per-card); a Web Crypto fast path can be added later behind the same
function if profiling justifies it (Power-of-Ten: correct first, fast second).
