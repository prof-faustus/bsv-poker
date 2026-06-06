# Architecture

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](./P2P_MODEL.md).

One deterministic **core**, two **shells** (Node desktop + browser web), and three **loopback
services** (relay, indexer, and the embedded BSV node). The core is a pure function of its inputs —
no I/O, no time, no randomness — so two honest clients given the same valid inputs converge to
byte-identical state (P2, the cross-client agreement guarantee).

## Package / app map

### Deterministic core (pure; runs in Node AND the browser)
| Package | Role |
|---|---|
| `protocol-types` | Canonical serialization, deterministic SHA-256, shared types, and the hardened primitives (`safe.ts`, `reader.ts`). |
| `engine` | The game-module FSM framework (`GameModule`, `replay`, timeout-eligibility). |
| `game-holdem` / `game-omaha` / `game-stud` / `game-razz` / `game-draw` / `game-blackjack` | Per-variant rules as pure `GameModule`s. |
| `hand-eval` | Hand ranking / showdown evaluation. |

### Crypto / chain (Node-side security-critical path)
| Package | Role |
|---|---|
| `crypto-mentalpoker` | Real commit/reveal, secret-permutation shuffle, combined per-card keys (secp256k1). |
| `script-templates-ts` | On-chain script templates, signing, and the Script interpreter. |
| `tx-builder` | Transaction model, wire serialization, **parser**, BIP-143 sighash, fallback graph. |
| `wallet-custody` | One long-term key per player; HKDF-derived per-game/per-card scalars (Mode A/B). |
| `adapters` | Conformance-bound contracts (CT/BS/VA/OB) + the real BSV node client. |
| `sdk` | The assembled SDK surface. |

### Application
| Package / app | Role |
|---|---|
| `app-services` | Lobby, networked table clients, session auth, transcript rebuild, persistence. |
| `ui-core` | View-models + components (menu-driven; a human selects every action). |
| `apps/client-desktop` | The native desktop shell (+ the Node-side settlement service). |
| `apps/client-web` | The browser play-money demo. |
| `apps/relay-go` | Transport-only relay (presence, tables, capability-gated fan-out). |
| `apps/indexer-go` | Per-table transcript projection (opaque or validating). |

## Data flow (one hand)

1. **Lobby** (`app-services/lobby.ts`): players discover a table over the relay, join with a
   **signed** join, and agree seats by a non-grindable beacon.
2. **Handshake**: each seat broadcasts a **signed** entropy `commit`, then `reveal`; the deck is the
   composition of the revealed secret permutations (mental poker — `CARD_AND_DECK_MODEL.md`).
3. **Play**: on their turn a human selects an action; the client applies it through the deterministic
   engine and broadcasts a **signed** `action` envelope binding the prior state hash.
4. **Transcript**: every envelope is dual-pathed to the indexer; a reconnecting client rebuilds state
   from the (authenticated, in validating mode) transcript.
5. **Settlement**: the on-chain path (Node-side) builds and signs the settlement / fallback
   transactions and submits them to the embedded node.

## Why this shape

- **Pure core** → determinism and replayability (the relay/indexer are never the source of truth).
- **Two shells over one core** → the browser and desktop run the identical engine bytes.
- **Loopback services, transport-only** → the relay/indexer hold no game truth; compromising them
  cannot change an outcome, only deny/garble service (which the client detects).

See `PROTOCOL.md`, `WIRE_FORMAT.md`, `STATE_MACHINE.md`, `ONCHAIN_MODEL.md` for each layer in detail.
