# ADR 0001 — Node-native TypeScript (type-stripping) for the deterministic core

**Status:** Accepted

**Context.** The core spec (§3.2) mandates one deterministic TypeScript core shared by web and
desktop. The build host has TLS-inspection that makes heavy dev-dependency installs fragile, and
the spec's Power-of-Ten discipline favours a minimal, auditable toolchain.

**Decision.** Run `.ts` directly via Node 24's native type-stripping; test with `node --test`;
typecheck with `tsc --strict --noEmit` only (no emit). No bundler, ts-node, or test framework is
used for the packages. To stay strip-compatible the core avoids `enum`, `namespace`, and
parameter-properties, and imports with explicit `.ts` extensions — which also aligns with the
Power-of-Ten "limit metaprogramming" adaptation (§13.1).

**Consequences.** Near-zero install surface for the core; the typecheck (`tsc`) is the single
source of type truth; tests run with no build step. The browser/desktop shells still use Vite/Tauri
toolchains (their own concern). `pnpm` installs use `NODE_OPTIONS=--use-system-ca` to trust the
host CA.
