# On-chain model

All transaction logic is BSV-native and lives in `BsvPoker.Core/Chain.cs`. The same code path serves
regtest, testnet, and mainnet — the network is only an address/version tag plus a node endpoint, never a
branch in the logic.

## Transactions

- **Model:** `Tx(version, inputs, outputs, lockTime)` with `TxIn(prevTxid, vout, scriptSig, sequence)`
  and `TxOut(value, script)`.
- **Serialization / txid:** standard little-endian wire format with `VarInt` lengths; `txid` is the
  big-endian hex of `SHA-256d` of the serialized transaction.
- **Scripts:** P2PKH locking script `OP_DUP OP_HASH160 <20> OP_EQUALVERIFY OP_CHECKSIG`. `OP_RETURN` is
  never produced anywhere in the project.

## The FORKID sighash

BSV uses the FORKID sighash with `SIGHASH_FORKID` set (hashtype `0x41` = `SIGHASH_ALL | SIGHASH_FORKID`).
`SighashForkId` builds the preimage — version, `hashPrevouts`, `hashSequence`, the outpoint, the
scriptCode and amount of the input being signed, sequence, `hashOutputs`, locktime, and the hashtype —
and double-SHA-256s it. This is the BSV consensus signature digest.

- **Signing:** `SignP2pkhInput` signs with secp256k1 (low-S, RFC-6979), DER-encodes, appends the
  hashtype byte, and sets `scriptSig = <sig+type> <pubkey>`.
- **Verifying:** `VerifyP2pkhInput` recomputes the digest with the input's scriptSig emptied (the sighash
  commits to the scriptCode, not the scriptSig), parses the signature, and checks it against the pubkey.
  A tampered amount changes the digest and fails verification (verified in tests).

## Unilateral recovery (always-recoverable)

Before a player risks any funds, they hold a **pre-signed nLockTime refund** of their own money:

```
BuildRecovery(fundingTxid, vout, amount, ownerSeed, ownerPub, fee, lockHeight)
```

- The single input uses a **non-final sequence** (`0xfffffffe`) so the transaction's `lockTime` actually
  binds; the `lockTime` is a future height.
- The single output pays `amount - fee` back to the owner's own P2PKH.
- The transaction is signed immediately, so the owner can broadcast it after `lockHeight` to reclaim
  funds unilaterally — no counterparty cooperation required, no satoshi stranded.

This is the on-chain backstop for the dealerless game: cooperation produces the normal settlement, and if
cooperation fails the recovery guarantees every player can get their funds back.

## 2-of-2 escrow: pot funding, cooperative settlement, unilateral recovery

Two players fund the pot into a **2-of-2 bare multisig** output
(`OP_2 <pubA> <pubB> OP_2 OP_CHECKMULTISIG`). From there:

- **Cooperative settlement** (`BuildCooperativeSettlement`): spends the escrow to the winner; both peers
  sign (`SignMultisig`) and the scriptSig is `OP_0 <sigA> <sigB>` (the `OP_0` is the CHECKMULTISIG dummy;
  signatures must be in pubkey order).
- **Unilateral recovery** (`BuildEscrowRecovery`): a refund that returns each funder's stake after a
  future `lockHeight`, with a non-final input sequence (`0xfffffffe`) so the locktime binds. Both peers
  **co-sign it before the escrow is funded**, so neither can strand the other's money — the
  always-recoverable rule applied to shared funds.

Verification (`VerifyMultisig2of2`) is **consensus-grade strict**: it bounds-checks the scriptSig, requires
the exact `OP_0` dummy, rejects trailing bytes, requires the hashtype byte to be exactly `0x41`, and parses
each signature with a **strict DER** reader (canonical integers — non-negative, no superfluous leading
`0x00`, a single sign byte allowed; no trailing data). Both signatures must verify against the two pubkeys
in order. This path is covered by positive and hostile-negative tests (tampered amount, outsider signature,
swapped order, wrong hashtype, trailing bytes, missing dummy, malformed DER).

## Broadcast

Live broadcast against a BSV node is on the roadmap. The funding/relay node is a **separate project**;
this client builds, signs, and verifies the transactions, and will submit them to a configured node
endpoint under the same code path for every network.
