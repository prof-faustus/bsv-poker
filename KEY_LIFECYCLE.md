# Key lifecycle

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

WHAT keys exist, HOW they are derived, WHY the design, and WHAT must never be assumed. Source of
truth: `packages/wallet-custody/src/custody.ts` and `packages/app-services/src/session-auth.ts`.

## Keys

| Key | Curve / type | Lifetime | Where |
|---|---|---|---|
| **Wallet master** | 32-byte secret (seed for secp256k1) | long-term, one per player | held in-process by the software custody backend; never exposed to the UI |
| **Per-game / per-card scalar** `w_(gid,j,role)` | secp256k1 scalar | single game | derived on demand from the master |
| **Session (seat) key** | Ed25519 | one session | derived from the wallet root; signs relay envelopes |

## Derivation (deterministic, auditable)

- **Per-card / per-game scalar** (`custody.ts` `deriveScalar`): `HKDF-SHA256(master, info = "<gid>:<j>:<role>")`,
  rejection-sampled to a valid secp256k1 scalar in `[1, n-1]` (a salt counter is incremented until the
  output is in range; throws after a bounded number of tries). The device stores **one** key;
  every spend key is derived, so old-game keys reveal nothing about new ones (REQ-WALLET-001/002).
- **Session key** (`session-auth.ts` `deriveSeatSeed` → `sessionAuthFromSeed`): a 32-byte sub-seed
  `SHA-256("bsv-poker/seat-ed25519" ‖ root)` becomes the Ed25519 private seed. Linking the seat seed
  to the **same** wallet root means a valid seat signature proves control of the root that funds the
  buy-in (audit-02 #3) — the seat identity and the funding key share one root.

## WHY this design (and why not the alternatives)

- **One stored key + deterministic derivation** rather than a key store of many keys: less secret
  material at rest, fully reproducible for dispute replay, and no key-management surface.
- **HKDF with full domain separation** (`gid`, card index `j`, `role`) rather than reusing a key or
  deriving from public data: reuse would link spends and can create unspendable or cross-game-
  confusable keys. Every derivation context field is mandatory.
- **Ed25519 for session signatures** rather than reusing the secp256k1 key directly on the wire: the
  long-term identity/funding key must not be spent directly as an on-chain key or exposed as a wire
  signer; the session key is a derived, single-session credential.

## WHAT MUST NEVER BE ASSUMED

- Never assume the master scalar is exposed to the UI — the custody contract returns **public** keys
  only (`derive` → compressed pubkey hex); scalars never cross to the viewer path (REQ-APP-025).
- Never reuse a derivation `info` across games/cards/roles — domain separation is the security.
- Never treat the session key as the funding key — it authenticates the seat; spends are signed by
  the derived secp256k1 scalars (Mode A sums the disclosed scalars; see `SPEND_KEY_DERIVATION.md`).

## WHAT BREAKS IF VIOLATED

Reusing a derivation context links spends and can strand funds; exposing a scalar to the UI leaks a
spend key; signing wire envelopes with the long-term key turns a transport signature into a
fund-control signature. See `SPEND_KEY_DERIVATION.md` for the on-chain spend path.
