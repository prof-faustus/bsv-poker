# ADR 0003 — Self-contained Genesis Script interpreter for Phase 0/1

**Status:** Accepted

**Context.** P9 (core §14.3) requires script spends to run through a **real** BSV Script
interpreter with Genesis rules, with negative tests failing **inside** the interpreter. The
embedded `bonded-subsat-channel` node's production interpreter is the eventual target, but it is
not yet bound, and the build must demonstrate P9 now.

**Decision.** Implement a real stack interpreter (`script-templates-ts/src/interpreter.ts`)
covering the opcode subset the templates use, with **real secp256k1 ECDSA** `OP_CHECKSIG`/
`OP_CHECKMULTISIG` (Node crypto), real hash/conditional/stack ops, CLTV/CSV as **no-ops**
(REQ-TX-001), and `OP_RETURN` rejected (core P11). Negative spends fail inside it.

**Consequences.** P9 is met today with a genuine interpreter (not signature spot-checks). Two
documented divergences from the production node, tracked as `TRACKED ASSUMPTION`: (1) the sighash
is ECDSA-over-SHA-256 of the preimage rather than BIP-143-style double-SHA-256; (2) only the
template opcode subset is implemented. When the real node is bound, the same template tests re-run
against it unchanged; any divergence is then a defect to fix in the templates, not the tests.
