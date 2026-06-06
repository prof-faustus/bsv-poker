# Wallet and keys

The wallet is **BSV-native and deterministic**. The whole wallet is one **32-byte master seed**. Every
spending key is derived directly from that seed; backing up the seed backs up the entire wallet.

## Key derivation (`WalletKeys`)

A key for `(chain, index)` is:

```
d = HMAC-SHA256( key = seed, msg = "bsvpoker-key|{chain}|{index}|{salt}" )   reduced to a valid secp256k1 scalar
```

- `chain = 0` is the receive chain; `chain = 1` is the change chain.
- `salt` starts at 0 and is incremented only in the astronomically unlikely event the HMAC output is not
  a valid scalar, so derivation is total and deterministic.
- The result is a pure function of the seed: the same seed always reproduces the same wallet.

**Why this design.** It is the simplest construction that gives deterministic, independent keys from a
single secret, with explicit domain separation (`chain`/`index`/`salt` are encoded unambiguously inside
the HMAC message) and no hidden state. There are no extended keys, no chain codes, and no path notation
to misuse. Every key is a secp256k1 key — the BSV curve, end to end.

## Backup and restore

The entire backup is one string:

```
SeedToBackup(seed)  =  Base58Check( 0x9c ‖ seed )      # 0x9c distinguishes it from addresses/WIF
BackupToSeed(string) =  inverse, validates the checksum and the version byte
```

Restoring a wallet means pasting this seed string back in. A single-character corruption fails the
Base58Check checksum and is rejected — you cannot silently restore the wrong wallet.

## Password-at-rest (`WalletExtras.EncryptSeed` / `DecryptSeed`)

The seed can be encrypted on disk under a user password:

```
key  = PBKDF2-HMAC-SHA256( password, salt(16 random bytes), 250_000 iterations ) → 32 bytes
blob = AES-256-GCM.Seal( key, seedBackupString )
stored = "enc1." ‖ iterations ‖ "." ‖ base64(salt) ‖ "." ‖ base64(nonce‖ciphertext‖tag)
```

- A wrong password fails the GCM authentication tag — decryption throws, it does not return garbage.
- Fresh random salt and nonce per encryption, so encrypting the same seed twice yields different blobs.
- On startup an encrypted wallet prompts to unlock; the cleartext seed lives only in memory while
  unlocked, and the UI refuses seed-dependent operations (send, address, WIF, sign) while locked.

## What the wallet does today

- Derives and shows receive addresses (P2PKH, Base58Check of `HASH160(pubkey)`).
- Tracks balance and a transaction history, persisted atomically to the profile directory so closing
  never loses state.
- Exports a key as **WIF** and signs/verifies **Bitcoin signed messages**.
- Holds your card NFTs (see [MENTAL_POKER.md](MENTAL_POKER.md)).

The wallet is **fully standalone**: it generates the seed, derives keys, and builds/signs transactions
entirely offline, with **no connection to any node, RPC, or server**. Getting coins onto the chain is
external to the product (a separate testnet-funding node, for testing only) — see
[ONCHAIN_MODEL.md](ONCHAIN_MODEL.md).

## Per-instance identity

Each running copy claims its own profile directory with an exclusive file lock and stores its own seed
and identity key. Two copies on one machine are therefore two distinct players with distinct wallets.
