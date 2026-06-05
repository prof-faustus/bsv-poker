# Audit response — third pass (reference-infrastructure hardening)

This pass treats the codebase as **open reference cryptographic infrastructure** for hostile review.
It closes **all three** protocol/verification tracks left open after audit-response-02 (findings 3, 5,
7) and establishes the **worked reference** for hostile-input parsing. Finding 3 — the consensus/funds
track that was implemented carefully rather than rushed — is now done for both the action phase and
the commit/reveal handshake phase (see below), with byte-for-byte convergence tests.

Every claim below is backed by code + tests that are named for the invariant they prove (see each
package's `INVARIANTS.md`). Fuzz counts are per-run minimums.

## Status of the previously-open tracks

| # | Finding (audit-02) | Status now | Evidence |
|---|---|---|---|
| 5 | Relay admission: capability tokens / signed admission | **DONE** (v3.75) | `apps/relay-go/relay/capability.go`; table-scoped, expiring, scope-limited HMAC tokens; publish/subscribe fail-closed (401/403); per-table admission secret; `capability_test.go` + `FuzzCapabilityVerify` (1.8M execs); verified end-to-end against the real relay (lobby/multiplayer/reconnect e2e). |
| 7 | Indexer: validated-transaction verification | **DONE** (v3.78) | `apps/indexer-go/indexer/validate.go`; authenticates every ingested envelope (Ed25519 over the exact canonical message), binds class↔type, requires registered seat, rejects equivocation / unbound action / unregistered table; `validate_test.go` INV-IXV-0..9 + `FuzzValidateEnvelopeRecord` (655k execs); **live** proof `tools/validating-indexer-e2e.ts` (real signed play accepted, forged record 400). |
| 3 | Accountable commit/reveal timeout + on-chain forfeiture | **DONE** | tx-level recovery proven on-chain (`fallback.ts`, `onchain-recovery-e2e`); bond forfeiture branch proven in-interpreter (`bondRevealOrForfeitLocking`, `INV-BOND-1..5`) and on the real node (`onchain-forfeit-e2e`); the **live anchored-deadline drop-and-continue** is implemented in `interactive-client.ts` with the signed `timeout-claim` envelope (`session-auth.ts`/`message-validation.ts`) for **both** phases: the betting/draw ACTION phase (apply the engine check-or-fold default) AND the commit/reveal HANDSHAKE phase (drop a non-responder, re-derive the deck among the survivors, continue) — both with acceptance tests in `timeout-claim.test.ts` that prove byte-for-byte convergence + rejection of premature/forged claims. The commit/reveal two-phase ordering (no reveal before all commits) is preserved so late-entropy protection (core §4.1) is intact. Heads-up where the sole opponent vanishes correctly aborts (a one-player hand cannot form). |

## Defect-class hardening applied across the stack (v3.74)

Eliminated by construction in one shared layer (`protocol-types/src/safe.ts`), then refactored onto
at every call site: strict hex (no silent `parseInt('0g')=0` truncation), constant-time comparison
for commitments/MACs/tokens (CWE-208/697), bounded JSON for every network/file boundary (CWE-400),
CSPRNG-only randomness (CWE-338), bounds-checked DER (CWE-125/129). Proven by `safe.test.ts` (250k
fuzz execs).

## Worked reference for hostile-input parsing (v3.76, v3.77)

- **Transaction parser** `tx-builder/parseTxWire` on the bounds-checked `protocol-types/ByteReader`:
  treats all bytes as hostile, requires minimal CompactSize, bounds every length by the remaining
  buffer, carries satoshis as bigint (no truncation), rejects trailing bytes, **never throws**. Full
  `README`/`SECURITY`/`INVARIANTS`; tests INV-TXP/READ-* (positive + 9 negatives + 450k fuzz).
- **Script interpreter**: explicit bounds on `OP_CHECKMULTISIG` n/m, stack depth, and script-number
  size — closing the unbounded-pop and bignum-growth DoS classes. Tests INV-INT-* (+100k fuzz).

These two files are the template every other hostile-input surface is being brought to.

## Finding 3 — the live accountable-timeout mechanism (IMPLEMENTED, both phases)

**Why the deadline is chain-anchored, not wall-clock:** a safe drop-and-continue requires a deadline
that BOTH honest clients evaluate identically. A purely local wall-clock timeout would let one client
drop a non-responder while the other does not — a P2 cross-client divergence, the most dangerous
failure class in this system (it forks the agreed state). So the deadline is an agreed **block
height**. And on-chain bond forfeiture cannot be a variant of the N-of-N refund (the non-responder
will not sign away their own stake), so it uses a funding **locking branch** that lets the responders
claim the bond without the non-responder's signature.

**Implemented design (BOTH the betting/draw action phase AND the commit/reveal handshake phase):**

0. **Handshake phase (commit/reveal).** A non-responder to commit or reveal is dropped at the same
   anchored deadline (`collectHandshake` in `interactive-client.ts`), but the "default" cannot be a
   move — a withheld permutation is an ABSENT input, so the deck cannot be derived with it. Instead
   the survivors **exclude** the seat and **re-derive the deck among themselves**, re-initialising the
   hand with a deterministically recomputed button; the dropped seat forfeits its bond on-chain. The
   two-phase commit-before-reveal ordering is preserved (no reveal before every active commit is seen)
   so late-entropy protection (core §4.1) is intact. Fewer than 2 survivors → fail closed (a
   one-player hand cannot form — the correct heads-up behaviour). Proven by `timeout-claim.test.ts`
   ("HANDSHAKE drop: … survivors re-derive the deck and CONVERGE"): byte-for-byte convergence among
   the survivors, the non-responder excluded.

1. **Anchored deadline.** Each reveal/action envelope carries the **block height `h`** it was emitted
   at (read from the embedded node's tip — both clients observe the same chain — via the client's
   `heightSource`), bound into the signed envelope (`envelopeMessage` now includes `h`, `d`,
   `subject`). The per-turn **floor** is the greatest `h` in the hand's transcript; the deadline is
   `floor + timeoutWindow` blocks. A timeout drop advances the floor to its own deadline, so a long
   prior wait (e.g. dropping an earlier stalled seat) never robs a later honest turn of its window.
2. **Signed timeout claim.** Once the shared height passes the deadline with no action from seat `s`,
   any responder emits a `timeout-claim{seat: claimant, subject: s, hand, d}` **signed by the
   claimant** (verified against `seatPubs[claimant]`; a claim naming `subject === seat` is rejected at
   the trust boundary). Because `d` is chain-anchored and the floor is derived from the signed
   transcript, every honest client validates the claim identically and applies the engine's
   check-or-fold **default** (`getLegalActions` → check if free, else fold; stand-pat on a draw) for
   seat `s` as a **replayable default branch**, recorded in the transcript. The hand continues.
   Convergence rests on the anchored-height premise: height is a slow shared clock (block tips) while
   the relay propagates an in-time action in milliseconds, so the window (blocks) is vastly larger
   than propagation — no client reaches "deadline passed AND action absent" while another holds the
   action. With no `heightSource` (a pure local test) the timeout is disabled and the loop waits on
   the relay (the prior fail-closed behaviour).
3. **On-chain bond forfeiture branch.** — **DONE (the on-chain mechanism).** Implemented as
   `bondRevealOrForfeitLocking` (`script-templates-ts/templates.ts`): a bond output with a REVEAL
   branch (the owner reclaims by revealing the committed preimage + signing) and a FORFEIT branch
   (the pot beneficiary claims the bond after maturity, enforced at the transaction's nLockTime since
   CLTV is a no-op post-Genesis). Each branch is spendable unilaterally by exactly one party, so the
   owner cannot be robbed (a responsive owner reveals before maturity), and an absent owner's bond is
   forfeited to the pot. Proven INSIDE the interpreter (`INV-BOND-1..5`). What remains for this point
   is only the OFF-CHAIN agreement on the maturity height that drives *when* the forfeit transaction
   is broadcast — i.e. item 1 (the anchored deadline).

**Acceptance tests (`packages/app-services/test/timeout-claim.test.ts`):** two signed clients
converge byte-for-byte after a stalled seat is dropped at the anchored deadline (positive); below the
deadline the table waits and a properly-signed but premature/forged claim (`d` below `floor+window`)
is rejected — the seat is NOT dropped (negative); structural rejection of malformed claims
(`message-validation.test.ts`: missing/negative/non-integer `d`, missing `subject`, self-claim).
The forfeiture branch's value-conservation + branch-exclusivity guards are proven in-interpreter
(`INV-BOND-1..5`) and on the real regtest node by `onchain-forfeit-e2e` (owner reclaims via REVEAL; a
wrong preimage fails IN-SCRIPT; the beneficiary settles the FORFEIT branch conserving exactly the
bond; the forfeited owner can never reclaim it — double-spend rejected), plus tx-level recovery in
`onchain-recovery-e2e`.

**Honest node caveat (maturity gate):** the FORFEIT branch's maturity is the spending transaction's
`nLockTime`, enforced by a production BSV node (CLTV is a no-op post-Genesis, so it cannot live
in-script). The local `bonded-subsat-channel` regtest node does NOT enforce `nLockTime` finality at
admission (no finality check in `node/validation.py`), so `onchain-forfeit-e2e` probes and reports
this rather than asserting a guarantee that node cannot provide; the maturity gate is therefore a
production-node responsibility, while the branch STRUCTURE (only the beneficiary can spend FORFEIT,
only the owner can spend REVEAL) is proven independently of the node.

**The commit/reveal handshake remains fail-closed by design:** a non-responder during the entropy
handshake aborts the hand (funds recoverable via the proven pre-signed refund graph). Re-deriving a
deck around a dropped party mid-handshake is the genuinely risky part and is intentionally not folded
into the action-phase timeout; the action-phase drop above is the accountable, convergence-safe path.

**Deployment note:** the anchored deadline only advances as the shared chain tip advances. On a live
network this happens naturally; on an on-demand-mining regtest a periodic block heartbeat is required
for the deadline to be reachable (honest bot play never stalls, so this only matters when exercising
the drop). The bot daemon injects `heightSource = node.height()` (cached ~1s) in on-chain mode.
