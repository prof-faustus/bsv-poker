# Security model

This client is intended to be read as open reference crypto infrastructure: the goal is to make any
weakness easy to find. This document states the properties the code aims to provide and how they are
enforced, so each claim can be checked against the code and the tests.

## Cryptographic primitives

- **secp256k1 only** (the BSV curve). ECDSA uses RFC-6979 deterministic nonces and enforces **low-S**, as
  BSV consensus requires. Ed25519 is not used anywhere.
- **Hashing:** SHA-256, SHA-256d, RIPEMD-160, and HASH160. RIPEMD-160 is re-implemented because .NET
  removed it; it is covered by known-answer tests.
- **Authenticated encryption:** AES-256-GCM (`nonce‖ciphertext‖tag`, fresh random nonce per message).
  Wrong key or tampered ciphertext fails the tag and throws — it never returns unauthenticated data.
- **Key derivation:** HKDF-SHA256 for message keys; PBKDF2-HMAC-SHA256 (250k iterations) for the
  password that protects the wallet seed at rest.

## Properties and how they are enforced

| Property | Mechanism |
|----------|-----------|
| No party controls the deck | Commit-before-reveal + composition of all players' unbiased permutations (`MentalPoker`). |
| Shuffle is unbiased | Rejection-sampled Fisher–Yates over an HMAC counter stream; the rejection bound is computed in 64-bit to avoid overflow. |
| Hole cards belong to one player | Cards are sealed (ECIES over secp256k1) to the owner; only their key opens them (`CardNft`). |
| Chat confidentiality, no key reuse | Fresh ephemeral ECDH key **per message per recipient** → HKDF → AES-256-GCM; the wire is ciphertext. |
| Hole cards are private during play | Networked deal uses commutative-encryption masking (`MentalPokerEC`): each peer can unmask only its own holes; the board is revealed per street, opponents' holes only at showdown. Proven 2-player and 3-player. |
| Funds are always recoverable | Each player pre-signs a unilateral nLockTime refund of 100% of their funds before risking any (`Chain.BuildRecovery`); shared pots use a co-signed 2-of-2 escrow recovery (`Chain.BuildEscrowRecovery`). |
| Signatures are consensus-valid | Low-S ECDSA, FORKID sighash, DER + hashtype; verification recomputes the digest from scriptCode. |
| No `OP_RETURN` | Card NFTs bind data with `OP_DROP`; no code path emits `OP_RETURN`. |
| A second instance is a different player | Per-instance profile claimed via an exclusive file lock, with its own seed and identity. |
| Clean termination, funds returned | `ShutdownMode.OnMainWindowClose` + a hard `OnExit` exit; no orphan processes. |

## Network and transport

- The transport is a peer-to-peer gossip mesh with **no server**. It binds to **loopback by default**;
  exposing it to the LAN/Internet is an explicit user opt-in.
- **Every game message is authenticated**: signed by the sender's identity key and bound to the table,
  hand, and seat — a peer cannot act, reveal, shuffle, or commit on behalf of any seat but its own, and
  forged/unsigned/replayed frames are rejected. `hello` is a proof-of-possession of the identity key.
- **Directory and presence announcements are signed**; presence is signed by the player's own key, so no
  one can announce presence as another player. Unsigned/invalid announcements are dropped.
- Resource-exhaustion defences: connection cap, **byte-accurate** frame caps, **byte-cost** rate
  limiting, topic/payload caps, and anti-eviction. Every drop is counted with a reason.
- Chat messages are end-to-end encrypted (fresh ephemeral key per recipient per message); the mesh sees
  only ciphertext. The card deal is privacy-preserving by construction (`MentalPokerEC`).
- A deal or reveal that stalls (a peer withholding) triggers an **accountable abort** rather than hanging.

## Known limitations (tracked, not hidden)

- **Seat-order grinding is not fully mitigated.** Message forgery and seat spoofing are prevented
  (proof-of-possession + seat binding), but a determined attacker could still generate many valid keys to
  influence the sorted-pubkey seat ordering. A stake-binding / post-admission joint-randomness step is the
  next hardening for that specific residual.
- **On-chain broadcast** depends on a configured BSV node (the node is a separate project). The client
  (`NodeClient`) and a wallet "broadcast signed tx" action are built and tested against a mock node; the
  transactions (P2PKH and the 2-of-2 escrow, settlement, and recovery) are built, signed, and strictly
  verified in-process. End-to-end settlement on a live network requires pointing the client at a node.

## Red-team / audit

Red-team hardening is performed **after** the game is functionally complete, not before — always last,
and only when explicitly authorized. That phase has now run. Findings fixed, each with a positive and a
hostile-negative test:

| Finding | Fix | Test |
|---------|-----|------|
| Unsigned game messages / forged actions | Sign + verify every message | forged/unsigned `act` rejected |
| Spoofable seats / commits / reveals | Seat binding (signer must own the seat) + proof-of-possession join | wrong-seat / forged frames rejected |
| Binds to all interfaces by default | Loopback by default; explicit LAN opt-in | loopback-default + rebind test |
| Unsigned presence / table announcements | Signed + verified; presence signed by the player's own key | unsigned table rejected, signed propagates |
| Char-counted frames / frame-count rate limit | Byte-accurate frame caps + byte-cost rate limiting + recorded drops | oversize frame dropped, link resyncs |
| Lax DER on P2PKH | Strict canonical DER + strict scriptSig + exact hashtype | malformed DER / trailing bytes rejected |
| No commit/reveal abort path | Accountable abort timeout (→ nLockTime recovery with real funds) | a stalled deal aborts, no hang |

Residual (tracked above): seat-order grinding by mass key generation.
