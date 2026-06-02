# §19.C — Script template byte schedule + fair-play measurement + per-hand envelope

**Provenance (P6/P10):** every byte size below is **measured by running code** (`pnpm reproduce`
→ `spec/vectors/reproduce.json` → `templateWireBytes`), not asserted. Regenerate and verify with
`pnpm reproduce`.

## Measured template wire-byte sizes (locking scripts, Phase-1 builders)

| Template | Locking-script bytes | Notes |
|---|---|---|
| branch-binding prefix (`<bind> OP_DROP`) | **112** | 109-byte binding (gid8+rh32+round4+sh32+seat1+succ32) + pushdata + OP_DROP; pushdata-not-OP_RETURN (REQ-TX-010) |
| fold | **147** | binding + `<pub> OP_CHECKSIG` |
| funding 2-of-2 | **183** | binding + `OP_2 <pub><pub> OP_2 OP_CHECKMULTISIG` |
| reveal-or-timeout | **220** | binding + `OP_IF OP_SHA256 <cmt> OP_EQUALVERIFY <pub> OP_CHECKSIG OP_ELSE <pub> OP_CHECKSIG OP_ENDIF` |
| settlement | **147** | binding + `<winnerPub> OP_CHECKSIG` |
| **fair-play (per card)** | **175** | binding + `OP_IF OP_DUP OP_HASH160 <cmt> OP_EQUALVERIFY OP_CHECKSIG OP_ELSE <pub> OP_CHECKSIG OP_ENDIF` |

## Fair-play scaling decision (REQ-CRYPTO-009 / RT-01 M3)

GB2616862's worked fair-play script is a long nested `OP_IF/OP_ELSE` for **3 elements / 2 parties**.
A single 52-card N-party in-script EC-point-derivation proof may be very large; post-Genesis BSV
has no script-size cap so it is not impossible, but its byte size/fee/constructibility are
unverified. **Decision: ship the per-card / per-batch fair-play transaction structure** (the
measured 175-byte per-card script above is the implemented fallback, REQ-CRYPTO-009). The full
in-script EC-derivation proof is the upgrade once the embedded node's interpreter exposes the EC
numeric opcodes — its byte size remains a **TRACKED ASSUMPTION** until measured there.

## Per-hand transaction-count envelope (heads-up Hold'em; structurally derived from §19.E)

`reproduce.json → perHandTxEnvelope`: 1 funding + 2 entropy commits + 2 shuffle-stage commits +
1 deal + 3 board reveals + per-action bets + fold/settlement + **52 per-card fair-play**. The
transaction **count** is derivable now; **byte totals stay TRACKED ASSUMPTION** until the embedded
node's full interpreter measures them in CI (the platform's interpreter here uses an
ECDSA-over-SHA-256 sighash; production swaps the node's double-SHA-256 sighash).
