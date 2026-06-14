# Architecture

`poker.exe` is a single self-contained WPF application built from four .NET projects with a strict
dependency direction: `App → Net → Core → Crypto`. Nothing depends upward; the crypto layer has no
knowledge of poker, and the core game logic has no knowledge of the network or UI.

```
BsvPoker.App   (WPF UI, profiles, vault, views)
      │
BsvPoker.Net   (P2P gossip mesh, networked game protocol, encrypted chat)
      │
BsvPoker.Core  (cards, mental poker, hand eval, engine + variants, wallet keys,
      │         BSV transactions + FORKID sighash + recovery, card NFTs, bot)
      │
BsvPoker.Crypto (secp256k1, RIPEMD-160/HASH160/SHA-256d, Base58Check, AES-256-GCM + HKDF)
```

## BsvPoker.Crypto

Dependency-free primitives.

- **`Secp256k1`** — pure C# (`System.Numerics.BigInteger`) over the BSV curve: ECDSA with a CSPRNG random nonce (rejection-sampled) and
  **low-S** (BSV consensus), digest-level `SignDigest`/`VerifyDigest`,
  compressed public-key derivation, ECDH, DER encoding, and key generation. Jacobian point math with a
  single field inversion per scalar multiply. Verified against known vectors in the test suite.
- **`Ripemd160` / `Hashes`** — RIPEMD-160 (re-implemented because .NET removed it), plus `Sha256`,
  `Sha256d`, and `Hash160` (RIPEMD-160 of SHA-256).
- **`Base58` / `Base58Check`** — address and key/seed encoding with checksum.
- **`Aead`** — AES-256-GCM seal/open (`nonce‖ciphertext‖tag`, fresh random nonce) and HKDF-SHA256.

## BsvPoker.Core

The game and money logic — no I/O.

- **`Cards`** — 52-card model and deck.
- **`MentalPoker`** — dealerless shuffle: each player commits `SHA-256(entropy)` before anyone reveals,
  the deck order is the composition of every player's unbiased Fisher–Yates permutation, and a combined
  per-card key is a real secp256k1 point bound to the combined seed. See
  [MENTAL_POKER.md](MENTAL_POKER.md).
- **`HandEval`** — best five-card hand, including per-variant rules (`BestForVariant`).
- **`HoldemEngine`** — blinds, betting, all-in **side pots**, showdown, chip conservation.
- **`Variant`** — the six games and their deck/hole-card rules.
- **`WalletKeys`** — BSV-native deterministic keys from one master seed. See
  [WALLET_AND_KEYS.md](WALLET_AND_KEYS.md).
- **`Chain`** — BSV transaction model, serialization, txid, P2PKH scripts, the FORKID sighash, input
  signing/verification, and the pre-signed nLockTime recovery builder. See
  [ONCHAIN_MODEL.md](ONCHAIN_MODEL.md).
- **`CardNft`** — cards as 1-satoshi NFTs sealed (ECDH + AES, owner-only) to their owner; transfer re-seals so the
  sender loses access. No `OP_RETURN`.
- **`WalletExtras`** — WIF import/export, Bitcoin signed messages, and password-based seed encryption.
- **`BotPolicy`** — a simple decision policy used only for the test/bot opponent.

## BsvPoker.Net

- **`P2PNode`** — a TCP gossip mesh: flood-with-dedup delivery, a serverless table/presence directory,
  a connection cap, per-peer inbound rate limiting, and anti-eviction. No server.
- **`NetGame`** — the networked **N-player** protocol over a per-table channel: a commutative-encryption
  deal (`MentalPokerEC`) for true hole-card privacy, anti-grinding seat assignment by joint-randomness
  commit-reveal (`SeatOrder`), per-street board reveals, and showdown reveals, then betting actions applied
  through the shared engine. The variant and seat count travel inside the table id (`t-<hex>~<Variant>~p<N>`)
  so peers agree with no extra message. Every message is signed by the sender's identity key and bound to
  the table/hand/seat (see [SECURITY.md](SECURITY.md)).
- **`ChatService`** — direct and group encrypted messaging; persists history per profile.

## BsvPoker.App

- **`MainWindow`** — wires the profile, node, vault, and the four tab views together; heartbeats
  presence on the mesh.
- **`Profile`** — per-instance directory claimed via an exclusive file lock, holding the wallet and a
  persisted identity. This is why a second copy is a different player.
- **`CardVault`** — the player's owned card NFTs, sealed to their key and persisted.
- **`Views/`** — `WalletView`, `LobbyView`, `GameView`, `ChatView` (all built in code).
- **App lifecycle** — `ShutdownMode.OnMainWindowClose` plus an `OnExit` hard-exit guarantee no orphan
  process survives a window close.

## How a networked hand flows

1. A host creates a table in the **Lobby**; it gossips across the mesh with the chosen variant.
2. A peer connects by `IP:port`, sees the table, and joins; both enter the **Game** tab.
3. Seats are assigned by joint-randomness commit-reveal (`SeatOrder`): every player commits a nonce, then
   reveals it, and the order is `H(jointSeed‖pub)` — fair and ungrindable, identical on every peer.
4. Each peer publishes `SHA-256(entropy)` (commit), then reveals entropy; both compose the identical
   dealerless deck (`MentalPoker`).
5. The `HoldemEngine` drives betting and showdown; chips are conserved.
6. Cards dealt to a player are sealed to that player and stored in their **CardVault** (Wallet tab).
