# Security model

This client is intended to be read as open reference crypto infrastructure: the goal is to make any
weakness easy to find. This document states the properties the code aims to provide and how they are
enforced, so each claim can be checked against the code and the tests.

## Cryptographic primitives

- **secp256k1 only** (the BSV curve). ECDSA uses a CSPRNG RANDOM nonce (rejection-sampled, no deterministic-nonce scheme) and enforces **low-S**, as
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
| Hole cards belong to one player | Cards are sealed (ECDH + AES, owner-only) to the owner; only their key opens them (`CardNft`). |
| Chat confidentiality, no key reuse | Fresh ephemeral ECDH key **per message per recipient** → HKDF → AES-256-GCM; the wire is ciphertext. |
| Hole cards are private during play | Networked deal uses commutative-encryption masking (`MentalPokerEC`): each peer can unmask only its own holes; the board is revealed per street, opponents' holes only at showdown. Proven 2-player and 3-player. |
| Seat order cannot be ground | Seats are ordered by a JOINT nonce seed — every admitted player commits a random nonce, then reveals it; the order is `H(jointSeed‖pub)` (`SeatOrder`, wired into `NetGame` seat assignment). The seed depends on every player's nonce, revealed only after all commitments lock, so no player can bias their position by generating many keys or by choosing their own nonce. All peers derive the identical order (consensus). |
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

- **The client is its own BSV node — no central server/RPC.** It peers directly with the decentralized
  network (DNS seeds → handshake → header validation → SPV funding → broadcast over peers). Verified live:
  connects to mainnet/testnet and validates real headers. Not yet done: header sync to the tip and a
  broadcast that gets mined (see `RED_TEST.md`). It builds, signs, and strictly verifies every transaction
  itself (P2PKH, 2-of-2 escrow, settlement, recovery).

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
| Seat-order grinding by mass key generation | Joint-randomness seating: every player commits then reveals a nonce; order = `H(jointSeed‖pub)` — no key/nonce choice can bias a seat (`SeatOrder` wired into `NetGame`) | `NetGameJoinTests` (both peers agree on the fair order) + `SeatOrderTests` (same pub lands in different seats under different seeds; non-matching reveal rejected) |

All red-team findings to date are fixed (each with a positive and a hostile-negative test).
