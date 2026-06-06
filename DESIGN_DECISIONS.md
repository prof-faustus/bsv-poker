# Design decisions

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

ADR-style record of the load-bearing choices: WHAT was decided, WHY, and which alternatives were
rejected and why. The point is to expose the reasoning so it can be challenged.

## DD-1 — Defect classes removed in one shared primitive layer, not per call site

**Decision:** one hardened layer (`protocol-types/safe.ts` + `reader.ts` + strict `hexToBytes`) for
hex, constant-time compare, bounded JSON, CSPRNG, DER; every call site uses it.
**Why:** the mission-critical bar is "defect classes impossible by construction", not "find bug →
patch". Centralising makes the class gone everywhere at once and auditable in one place.
**Rejected:** per-site fixes (one refactor reintroduces the class; an auditor must re-check every site).

## DD-2 — Parsers return discriminated results and never throw on hostile input

**Decision:** `parseTxWire`, `safeJsonParse`, `ByteReader`, `readDerEcdsaSig` return `{ok:false}` /
`null`, not exceptions.
**Why:** a malformed input must never abort the caller (SANS/CWE); the NASA P10 rule is to check every
return value; a `null`/`{ok:false}` forces handling in plain sight.
**Rejected:** throwing + a broad `try/catch` (hides control flow, swallows unrelated bugs).

## DD-3 — Satoshis as `bigint`; minimal CompactSize; no trailing bytes

**Decision:** the tx parser carries values as `bigint`, requires minimal CompactSize, and rejects
trailing bytes.
**Why:** prevent 2^53 truncation (CWE-190) and transaction malleability (two byte strings, one
meaning).
**Rejected:** `Number` values (truncate) and lenient encodings (malleable, dup-txid vector).

## DD-4 — The script interpreter bounds every attacker-controlled loop/value explicitly

**Decision:** `MAX_MULTISIG_KEYS`, `MAX_STACK`, `MAX_SCRIPT_NUM_BYTES` checked **before** the work.
**Why:** an incidental terminator ("the pop will underflow eventually") is one refactor from infinite
and invisible to an auditor; an explicit constant bound is provable (NASA P10).
**Rejected:** relying on stack underflow to terminate the multisig pop loop.

## DD-5 — Capability tokens are required and fail-closed; per-table admission optional

**Decision:** the relay requires a table-scoped, expiring, scope-limited HMAC token on publish AND
subscribe; a gated table additionally needs an admission secret to mint.
**Why:** envelope signatures stop action forgery, but the channel still needs admission control
(anti-spam, anti-poisoning) and selective access; fail-closed is the secure default.
**Rejected:** open channels (any process can read/poison a table); identity-bound tokens only
(heavier, and the seat identity is already the Ed25519 envelope signer).

## DD-6 — The indexer authenticates envelopes but does NOT re-implement the game engine

**Decision:** validating ingest checks authenticity, structure, binding, and non-equivocation; it does
**not** check game legality/settlement.
**Why:** a second poker engine in Go would inevitably diverge from the canonical TS engine on some edge
case — and a divergence between the ingest validator and the client engine is a **consensus split**,
strictly worse than no second check. The client replays the *authenticated* transcript through the one
canonical engine.
**Rejected:** full game-rule validation in the indexer (duplicate engine, consensus-fork risk).

## DD-7 — Accountable non-responder drop uses a shared anchored deadline (IMPLEMENTED)

**Decision:** the drop-and-continue is keyed on a **chain block height** both clients observe, never a
local clock; it is applied via a signed `timeout-claim` and is implemented for BOTH the action phase
(engine check-or-fold default) and the commit/reveal handshake phase (drop + re-derive the deck among
survivors). The non-responder forfeits its bond on-chain (`bondRevealOrForfeitLocking`).
**Why:** if one honest client dropped a non-responder and the other did not, the agreed state would
fork (P2 break) — the most dangerous failure class here. An anchored block-height deadline is a slow,
shared clock while the relay propagates an in-time action in milliseconds, so no client reaches
"deadline passed AND action absent" while another holds the action — both compute the identical drop.
**Rejected:** a local wall-clock timeout drop (non-deterministic, forks state). For the handshake,
also rejected: keeping a withheld permutation's slot (the deck cannot be derived without it) — instead
the survivors re-derive among themselves. Heads-up where the sole opponent vanishes still fails closed
(a one-player hand cannot form). Proven: `timeout-claim.test.ts` (convergence + premature/forged
rejection), `onchain-forfeit-e2e` (the on-chain penalty).

## DD-8 — One stored key + HKDF derivation with full domain separation

**Decision:** one long-term key per player; per-(gid, card, role) scalars via HKDF; Ed25519 session
key derived from the same root.
**Why:** least secret material at rest, deterministic and replayable for disputes, old-game keys
reveal nothing; the seat identity and funding key share one root so a seat signature proves funding
control.
**Rejected:** a key store of many independent keys (management surface) and deriving spend keys from
public data (linkable/guessable).

## DD-9 — Documentation and tests are co-equal with code

**Decision:** every security-relevant module carries WHAT/HOW/WHY/boundary docs; every security claim
maps to a positive + negative + fuzz test; the doc trio is CI-enforced.
**Why:** this is open reference infrastructure for hostile review — undocumented or untested-as-hostile
code is treated as suspect. The code's job is to teach an attacker how it works and prove why attacks
fail.
**Rejected:** "the code is the documentation" (an auditor cannot see intent, boundaries, or the WHY).
