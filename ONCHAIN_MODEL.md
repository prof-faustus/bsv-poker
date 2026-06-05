# On-chain model

The transaction classes, the anti-replay binding every transaction carries, the sighash, and the
recovery (fallback) graph. Source: `packages/protocol-types/src/tx.ts`,
`packages/tx-builder/src/{txbuilder,wire,parse,fallback}.ts`,
`packages/script-templates-ts/src/templates.ts`.

## BSV / Genesis rules

- **BSV only, post-Genesis.** `OP_CHECKLOCKTIMEVERIFY` / `OP_CHECKSEQUENCEVERIFY` are NO-OPs; all
  timing is **transaction-level** (`nLockTime` + `nSequence`).
- **`OP_RETURN` is banned everywhere.** Every commitment is pushdata in a *live* script
  (`<data> OP_DROP`), so the spend path actually executes it. CI fails if opcode `0x6a` appears in any
  script (`tools/lint-opreturn.ts`).

## Transaction classes (`tx.ts` `TX_CLASSES`)

`Funding`, `Commitment`, `Deal`, `Action`, `Timeout`, `Reveal`, `Fold`, `FairPlay`, `Settlement`,
`Recovery`, `TableMgmt`. These are conceptual classes; wire names are an implementation detail.

## Anti-replay branch binding (`tx.ts` `BranchBinding`)

Every protocol transaction binds, as pushdata in a live script (never `OP_RETURN`):

`gid` · `rulesetHash` · `round` · `stateHash` (the state being spent) · `actingSeat` ·
`successorCommitment` (commitment to the successor state).

This makes a signed branch valid only at one position in one game's state graph: replaying it against
a different state fails the binding (`script-templates-ts/test/templates.test.ts`, THR-PROTO-1).

## Sighash (`wire.ts`)

Real BIP-143 (FORKID) preimage. The value handed to `OP_CHECKSIG` / the signer is
`sha256(bip143Preimage)`; because the interpreter applies ECDSA-over-SHA256 to it, the effective
signed digest is `double-SHA256(preimage)` — the real BSV sighash. `SIGHASH_ALL | SIGHASH_FORKID`
(0x41). Signatures are **low-S DER** (the node rejects high-S).

## Wire serialization & parsing

`serializeTxWire` (Tx → canonical bytes) and the hardened `parseTxWire` (bytes → `ParsedTx`, never
throws) are inverses on canonical transactions. See `WIRE_FORMAT.md` and the worked reference in
`packages/tx-builder` (parser + `SECURITY.md` + `INVARIANTS.md`).

## Recovery: the pre-signed fallback graph (`fallback.ts`, REQ-TX-008, P4)

Before play, the N contributors to a funded pot co-sign a **timeout-default** refund that returns each
stake. It carries a LOW (non-final) `nSequence`, so a later **cooperative** settlement (higher
sequence, up to `0xffffffff` final) supersedes it under the original-replacement rule. Demonstrated
on the regtest node by `tools/onchain-recovery-e2e.ts` (two exits: low-seq timeout-default vs high-seq
cooperative). Refund outputs are value-conserving (the first contributor absorbs the rounding
remainder).

## Fair-play forfeiture (`templates.ts` `fairPlayLocking`)

An `IF claim / ELSE refund` branch: the claim path reveals the committed key, requires
`HASH160(pub) == keyCommitment`, then a signature under that key — so a party who used a different key
than it committed cannot redeem, and the bonded funds are forfeited. Honest play is the rational
outcome with no referee (REQ-CRYPTO-006/009).

## Accountable non-responder bond forfeiture (`bondRevealOrForfeitLocking`)

Forfeiting an absent player's **bond** (as opposed to the fair-play violation above) uses a bond
locking branch with two unilateral exits: REVEAL (the owner reclaims by revealing the committed
preimage + signing) and FORFEIT (the pot beneficiary claims past a chain-anchored maturity — the
spending tx's `nLockTime` — without the owner's signature). A responsive owner reveals before
maturity and reclaims; an absent owner's bond is forfeited to the pot. Proven in-interpreter
(`INV-BOND-1..5`) and on the real regtest node (`onchain-forfeit-e2e`: REVEAL reclaim, in-script
wrong-preimage failure, FORFEIT settlement + exact value conservation, post-forfeit double-spend
rejection). The OFF-CHAIN decision of *when* maturity is reached is the anchored-deadline mechanism
in `interactive-client.ts` (`STATE_MACHINE.md`). The maturity gate itself is a production-node
guarantee (this regtest node does not enforce `nLockTime` finality — see `FAILURE_MODES.md`).
