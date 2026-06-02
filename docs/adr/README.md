# Architecture Decision Records

Per app spec §A15 (REQ-APP-150) and the "no hidden assumptions" rule (core P7): every
significant decision taken during the build is recorded here with context, decision, status,
and consequences. These complement the declared-decision tables in the specs (core §0.5 D1–D9,
app §A0.3 AD1–AD10, §A20.1 AD-OPEN-*).

| ADR | Title | Status |
|---|---|---|
| [0001](0001-node-native-typescript.md) | Node-native TypeScript (type-stripping) instead of a bundler/ts-node | Accepted |
| [0002](0002-portable-sha256.md) | Portable pure-TS SHA-256 in protocol-types | Accepted |
| [0003](0003-self-contained-interpreter.md) | Self-contained Genesis Script interpreter for Phase 0/1 | Accepted |
| [0004](0004-mode-a-signing.md) | Mode A (reconstruct-at-reveal) signing for Phase 1 | Accepted (core D9) |
| [0005](0005-engine-auto-advance.md) | Engine auto-advances cooperative reveal/deal phases | Accepted |
