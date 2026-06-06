# The peer-to-peer model — no servers, one model for every BSV network

This document is the authoritative description of how bsv-poker carries a game **without any central
server**, how funds are **always recoverable**, and why the **same model** runs on regtest, testnet,
and real (mainnet) BSV. It is co-equal with the code: every claim here maps to a runnable proof.

---

## 1. WHAT: there is no central server

bsv-poker is fully peer-to-peer. There is **no relay server, no indexer server, no lobby server, and
no signaling server**. The only infrastructure is the **decentralized BSV node/chain** (which every
participant can run themselves). Concretely, the following do **not** exist in this system:

- a central pub/sub relay that all players connect to (the former `apps/relay-go` — **deleted**);
- a central transaction indexer/projection service (the former `apps/indexer-go` — **deleted**);
- any URL a player must be handed to "join the server."

Everything a table needs — exchanging commits/reveals/actions, agreeing seating, discovering open
tables, reconnecting, settling on-chain, and recovering funds — happens **directly between peers** or
**against the chain**.

## 2. HOW: the gossip mesh transport

The transport is `packages/adapters/src/p2p-transport.ts` (`P2PTransport`), stdlib-only over
`node:net` TCP. Every node is equal (listener + dialer). The model:

- **Table channel.** A published frame is delivered to the node's OWN subscribers (the protocol relies
  on seeing its own commits, like an echo) AND flooded to every connected peer; each peer delivers
  locally and re-floods to its other peers, with per-frame dedup. So a message reaches the whole mesh
  over **any connected graph** — no full mesh required, no loops, no central hub. Proven across a
  non-fully-connected chain A—B—C in `tools/p2p-e2e.ts` and `packages/adapters/test/p2p-transport.test.ts`.
- **Serverless discovery.** Hosting a table gossips an ANNOUNCE on a reserved directory topic; every
  node keeps a TTL'd view of announces it has heard, so `listTables()` is the **gossiped directory**,
  not a server lookup. Announces are re-gossiped periodically (late joiners learn existing tables) and
  a freshly-connected node floods a directory QUERY to pull the current set immediately. Presence
  (player → dialable address) works the same way. Proven in `tools/p2p-lobby-e2e.ts`.

`P2PTransport` satisfies the `Relay` interface (`packages/app-services/src/network.ts`)
**structurally** — the clients program to the interface (`RelayChannel` for the in-hand client, `Relay`
for the lobby), never a concrete class — so a `LobbyClient`/`InteractiveNetworkedTableClient` runs
over it with **no cast** and no server.

### Rendezvous (finding the first peer)

The transport needs to know which peer to dial first. That bootstrap is supplied out-of-band (a peer
address), exactly like Bitcoin's seed nodes — it is **not** a server that runs the game. A
BSV IP-to-IP / Bitmessage-style serverless rendezvous layers on top without changing the transport.

## 3. HOW: the browser plays without being a peer

A browser **cannot open raw TCP sockets**, so it cannot itself be a node:net peer. The serverless
answer (no central relay, and no WebRTC **signaling server**) is the standard P2P pattern:

> **Each player runs their OWN local node**, and the browser talks only to localhost — exactly like a
> wallet talking to your own `bitcoind`.

`tools/local-node.ts` is that local node: a `P2PTransport` peer that ALSO exposes the exact HTTP/SSE
surface the in-tree browser `RelayClient` already speaks, **bound to 127.0.0.1 only**. Each browser
request is bridged to this node's `P2PTransport`, so the browser's publish/subscribe/discovery all
travel peer-to-peer. It is **one per player** and **loopback only** — it is the player's own node, not
a relay anyone shares. The two players' nodes connect to **each other** over the mesh.

Proven end-to-end in `tools/browser-transport-e2e.ts`: two `RelayClient`s (the SAME class the web
client uses) each point at a DIFFERENT local node; one browser hosts a table, the other discovers it
through its own node (gossiped across the mesh), and a full hand converges byte-for-byte — **no central
server**. The web client (`apps/client-web`) defaults to the player's own local node
(`http://127.0.0.1:8090`).

### WHY this and not browser-to-browser WebRTC

WebRTC still needs a **signaling** channel (SDP/ICE exchange) and typically STUN/TURN for NAT
traversal — i.e. more servers, or an out-of-band bootstrap anyway. The own-local-node model is the
cleanest genuinely-serverless design, is how real P2P apps with browser UIs work, and reuses the exact
transport the Node clients use (one code path, one set of proofs).

## 4. HOW: funds can never be stranded (the close condition)

**Life-critical invariant:** before a player risks a single sat, every player already holds a
pre-signed, **unilateral nLockTime recovery** that returns **100% of the funds** if the game closes
poorly (peer vanishes, dispute, crash, refusal to co-sign — ANY failure).

- `presignNlocktimeRecovery` (`packages/tx-builder/src/fallback.ts`) emits a fully-signed N-of-N spend
  of the funded pot back to the contributors with a **future nLockTime** + a **non-final input
  sequence** (so the locktime actually binds). It **fails closed** rather than emit an unprotected
  refund: a non-future locktime, a final sequence, a partial signer set, or a pot-consuming fee all
  throw.
- Because it is pre-signed N-of-N, **any one holder can broadcast it alone** after the locktime — no
  counterparty cooperation, no server.
- The in-tree node enforces `IsFinalTx`, so the recovery is **rejected before** the locktime (it
  cannot race an in-time cooperative settlement) and **admitted after** it. Proven in
  `tools/onchain-nlocktime-recovery-e2e.ts` (reject-before, recover-100%-after) and 5 fail-closed unit
  tests in `packages/tx-builder/test/fallback.test.ts`.
- **Recovery-before-risk ordering** is enforced in the live funding path: the funder builds the escrow
  tx but does NOT broadcast it; all seats co-sign the recovery; only once every seat holds the recovery
  is the funding broadcast. Asserted in `tools/bot-onchain-net-e2e.ts`.

## 5. WHY: one model for regtest, testnet, and real BSV

BSV consensus — BIP-143/FORKID sighash, `nLockTime` finality, script rules — is **identical** across
regtest, testnet, and mainnet. So the on-chain model (funding, recovery, settlement) is genuinely
network-independent: the network is **only** a configuration tag + which node you connect to, never a
branch in the protocol.

- `Network = 'play-regtest' | 'regtest' | 'testnet' | 'mainnet'` (`network-gate.ts`). The **gate** is
  the only network-dependent piece: regtest/testnet are test coins (ready without hardening, no ack);
  **mainnet** (the only network with real funds) is reachable ONLY behind the explicit acknowledgement
  token AND full production hardening (`production-readiness.ts`).
- `tools/network-uniform-e2e.ts` proves the recovery tx body, recovery sighash, and settlement sighash
  are **byte-identical** across {regtest, testnet, mainnet}, then runs the full funding → recovery flow
  on the in-tree node. If the model ever branched on network, the bytes would differ and the test fails.
- The live path selects the network with `--network` (+ `--mainnet-ack` for mainnet); the funding,
  recovery, and settlement code is unchanged.

## 6. Security boundary

- **Authentication is at the peer.** Each node verifies an inbound envelope's Ed25519 signature against
  the acting seat's registered key BEFORE accepting it (`InteractiveNetworkedTableClient.subscribe`);
  forged/wrong-seat/unsigned frames are rejected (fail-closed). There is no server to trust — the peer
  is the trust boundary. Proven in `tools/validating-indexer-e2e.ts` (the "validating peer").
- **No central authority over admission.** Over P2P there is no capability-token gatekeeper; access
  control is the high-entropy table id + the signed commit-reveal seating + the per-seat session keys.
- **Loopback only for the local node.** The browser bridge binds 127.0.0.1; non-loopback binds are
  refused without an explicit opt-in (`resolveBindHost`, REQ-APP-106).
- **Bounded everything.** Frames are size-capped (1 MiB), the dedup set and directory are bounded
  (CWE-400), and untrusted frames flow through hardened parsers that never throw / never OOB-read.

## 7. What breaks / non-goals

- **A lone browser with no local node cannot play.** That is by design — being serverless means the
  player runs a node. The desktop host launches the local node for the player.
- **Directory freshness is eventual.** A just-joined node learns existing tables within one
  re-announce interval (or immediately via its directory query); `listTables()` is a gossiped view,
  not a strongly-consistent server list.
- **Mesh connectivity is the user's responsibility at bootstrap.** The first peer to dial is supplied
  out-of-band; once connected, gossip handles the rest over any connected graph.

## 8. Proof index (run any of these)

| Claim | Proof |
|---|---|
| Gossip reaches the whole mesh over a chain, exactly once | `tools/p2p-e2e.ts`, `packages/adapters/test/p2p-transport.test.ts` |
| Serverless discovery + signed seating + hand, no cast | `tools/p2p-lobby-e2e.ts` |
| Real multiplayer / 5 variants / session, peer-to-peer | `tools/multiplayer-e2e.ts`, `tools/multi-e2e.ts`, `tools/session-e2e.ts` |
| Reconnect from a PEER-held transcript (no indexer) | `tools/reconnect-e2e.ts` |
| Peer authentication + legality (no indexer server) | `tools/validating-indexer-e2e.ts` |
| Browser plays via its own local node | `tools/browser-transport-e2e.ts` |
| Unilateral nLockTime recovery of 100% of funds | `tools/onchain-nlocktime-recovery-e2e.ts` |
| Recovery held BEFORE funding (live path) | `tools/bot-onchain-net-e2e.ts` |
| On-chain co-sign settlement, peer-to-peer | `tools/bot-onchain-e2e.ts`, `tools/onchain-live-e2e.ts` |
| ONE model, byte-identical across networks | `tools/network-uniform-e2e.ts` |

All of the above are CI stages (`tools/ci.ts`).
