# Mental poker and card NFTs

## Dealerless shuffle (`MentalPoker`)

There is no trusted dealer and no single party that knows the deck order.

1. **Commit.** Before anyone reveals anything, each player publishes `SHA-256(entropy)` for a fresh
   32-byte random `entropy`. Committing first stops a player from choosing entropy after seeing others'.
2. **Reveal.** Each player reveals their `entropy`; everyone checks it against the commitment
   (`VerifyCommit`, constant-time compare).
3. **Compose.** Each player's entropy seeds an **unbiased** Fisher–Yates permutation of the deck. The
   draw stream is an HMAC-SHA256 counter stream; rejection sampling removes modulo bias (the rejection
   bound is computed in 64-bit to avoid the overflow that a 32-bit bound hits when it divides 2³²). The
   final deck order is the **composition** of every player's permutation, so no single player can fix it.
4. **Per-card keys.** `CombinedKey(seed, j)` is a real secp256k1 point derived from the combined seed,
   bound to card position `j` (rehashing on the rare invalid scalar).

This composed shuffle (`MentalPoker`) is used for the local practice/bot table, where one screen holds
all hands anyway. Tests verify the permutations are valid bijections and the shuffle is deterministic
given the same entropies.

## True per-card privacy for networked play (`MentalPokerEC`)

Networked play uses a stronger deal that keeps each hole card private — a **commutative-encryption**
scheme on secp256k1 (the BSV curve, no new dependency). Each card `i` is a fixed public curve point
`Mᵢ = (i+1)·G`. Masking a card is scalar multiplication, and because `a·(b·P) == b·(a·P)` the masks
**commute**, so players can mask in any order and strip them in any order:

1. **Shuffle pass** — in seat order, each player multiplies every card by one secret global scalar and
   applies a secret permutation. After everyone, the deck is `(∏c)·M_{σ(k)}` in a hidden order.
2. **Re-mask pass** — in seat order, each player removes their global scalar and re-applies an
   independent **per-card** scalar `d_k`. Now position `k` holds `(∏_p d_{p,k})·M_{σ(k)}` — its own key.
3. **Dealing** — to give position `k` to player T, every *other* player reveals only `d_{·,k}`; T strips
   them and its own mask to recover `M_{σ(k)}`. No one else can, because they lack T's secret `d_{T,k}`.
   The **board** is revealed by *all* players revealing their `d` at those positions; **opponent hole
   cards** are revealed only at **showdown**, when each player reveals the `d` at its own positions.

Privacy holds under the DDH-style hardness of the curve: a non-recipient who strips only the masks it
knows is left with `d_T·M`, which matches no base point. Tests prove this end to end — including a
**multiway (3-player)** simulation and a live 3-node networked hand — showing each peer reads only its
own hole cards, the board agrees across all peers, and showdown reveals every hand with chips conserved.

> **Honest scope.** This gives cryptographic *card privacy* against an honest-but-curious opponent. The
> network channel itself is **not yet authenticated** (messages are unsigned), so it is not secure
> against an *active* hostile peer who forges or replays protocol messages. Authenticating the transport
> is part of the (deferred) red-team hardening, not yet done — see [SECURITY.md](SECURITY.md).

## Cards as NFTs (`CardNft`)

Every card a player holds is a 1-satoshi NFT in their wallet.

- **Seal.** A card is sealed to its owner with **ECDH + an AES key** (no ephemeral key, no ECIES): the
  AES-256-GCM key is derived from the owner's own ECDH agreement (`ECDH(ownerPriv, ownerPub)`) with a
  fresh per-seal nonce, so only the owner can derive it; an opponent's sealed card cannot be read.
- **Lock script.** The NFT's locking script binds `H(sealed)` as pushed data with `OP_DROP`, followed by
  `<pubkey> OP_CHECKSIG`. It **never** uses `OP_RETURN`. Tests assert the script *structure*
  (`OP_PUSHDATA … OP_DROP … OP_CHECKSIG`) rather than scanning raw bytes, because a `0x6a` byte can
  legitimately appear inside pushed hash data.
- **Transfer.** Transferring a card re-seals it to the new owner, so the sender loses the ability to open
  it. Tampering with a sealed blob fails the AES-GCM tag and is rejected.

The owned cards are persisted per-profile in the **Card Vault** and shown in the Wallet tab, so closing
the app never loses them.
