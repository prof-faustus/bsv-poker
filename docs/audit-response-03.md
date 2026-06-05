# Audit response — third pass (reference-infrastructure hardening)

This pass treats the codebase as **open reference cryptographic infrastructure** for hostile review.
It closes the two protocol/verification tracks left open after audit-response-02, establishes the
**worked reference** for hostile-input parsing, and states the precise remaining design for the one
track that must not be rushed (it touches live consensus and on-chain funds).

Every claim below is backed by code + tests that are named for the invariant they prove (see each
package's `INVARIANTS.md`). Fuzz counts are per-run minimums.

## Status of the previously-open tracks

| # | Finding (audit-02) | Status now | Evidence |
|---|---|---|---|
| 5 | Relay admission: capability tokens / signed admission | **DONE** (v3.75) | `apps/relay-go/relay/capability.go`; table-scoped, expiring, scope-limited HMAC tokens; publish/subscribe fail-closed (401/403); per-table admission secret; `capability_test.go` + `FuzzCapabilityVerify` (1.8M execs); verified end-to-end against the real relay (lobby/multiplayer/reconnect e2e). |
| 7 | Indexer: validated-transaction verification | **DONE** (v3.78) | `apps/indexer-go/indexer/validate.go`; authenticates every ingested envelope (Ed25519 over the exact canonical message), binds class↔type, requires registered seat, rejects equivocation / unbound action / unregistered table; `validate_test.go` INV-IXV-0..9 + `FuzzValidateEnvelopeRecord` (655k execs); **live** proof `tools/validating-indexer-e2e.ts` (real signed play accepted, forged record 400). |
| 3 | Accountable commit/reveal timeout + on-chain forfeiture | **PARTIAL — see below** | tx-level recovery proven on-chain (`fallback.ts`, `onchain-recovery-e2e`); engine `isTimeoutEligible` default exists; the **live signed-deadline drop-and-continue** + **bond forfeiture branch** are specified below and intentionally not rushed. |

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

## Finding 3 — the remaining design (stated, not rushed)

**Why it is not yet implemented:** a safe drop-and-continue requires a deadline that BOTH honest
clients evaluate identically. A purely local wall-clock timeout would let one client drop a
non-responder while the other does not — a P2 cross-client divergence, the most dangerous failure
class in this system (it forks the agreed state). And on-chain bond forfeiture cannot be a variant of
the N-of-N refund (the non-responder will not sign away their own stake), so it requires a funding
**locking branch** that lets the responders claim the bond without the non-responder's signature.
Both must be designed deliberately; a hasty version risks stranded funds or a consensus fork — worse
than the current fail-closed abort.

**Required design (the next workstream):**

1. **Anchored deadline.** A commit/reveal/action round carries a deadline expressed as an agreed
   **block height** read from the embedded node (both clients observe the same chain), not local
   time. The deadline is bound into the signed envelope (extend `envelopeMessage`), so "the deadline
   was D" is non-repudiable.
2. **Signed timeout claim.** Past height D with no response from seat `s`, any responder emits a
   signed `timeout-claim{table, hand, seat:s, height:D}`. Because D is chain-anchored, every honest
   client validates the claim identically and applies the engine's `isTimeoutEligible` default
   (check-or-fold) for seat `s` as a **replayable default branch**, recorded in the transcript and
   accepted by the validating indexer as a first-class envelope type. The hand continues.
3. **On-chain bond forfeiture branch.** — **DONE (the on-chain mechanism).** Implemented as
   `bondRevealOrForfeitLocking` (`script-templates-ts/templates.ts`): a bond output with a REVEAL
   branch (the owner reclaims by revealing the committed preimage + signing) and a FORFEIT branch
   (the pot beneficiary claims the bond after maturity, enforced at the transaction's nLockTime since
   CLTV is a no-op post-Genesis). Each branch is spendable unilaterally by exactly one party, so the
   owner cannot be robbed (a responsive owner reveals before maturity), and an absent owner's bond is
   forfeited to the pot. Proven INSIDE the interpreter (`INV-BOND-1..5`). What remains for this point
   is only the OFF-CHAIN agreement on the maturity height that drives *when* the forfeit transaction
   is broadcast — i.e. item 1 (the anchored deadline).

**Acceptance tests to be written with it:** two clients converge after a peer is dropped at the
anchored height (positive); a client cannot drop a peer before D or with a forged claim (negative);
the forfeiture branch redistributes exactly the bond and conserves value (positive) and cannot be
claimed by the non-responder after maturity (negative); plus an on-chain `forfeiture-e2e` against the
regtest node.

Until then, the live handshake remains **fail-closed**: a non-responder aborts the hand (funds are
recoverable via the proven pre-signed refund graph), which is safe but not yet accountable.
