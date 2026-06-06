# BSV Poker — Complete Rebuild Specification (the contract)

The previous implementation is **rejected in full and will not be reused.** This document is the complete
contract for a from-scratch rebuild. Acceptance is **binary**: every item here must be present, correct,
and beautiful. One missing item = the whole thing is rejected. "Close" does not exist.

This is researched against the real Bitcoin/BSV network protocol, the Bitcoin SV node, and the Electrum /
Bitcoin Core wallet feature sets — the client must have **everything** those have.

---

## 0. Absolute, non-negotiable rules

1. **The client *is* a node.** `poker.exe` is itself a **full, non-mining, listening BSV node** that
   speaks the real Bitcoin P2P wire protocol and **peers directly with the decentralized BSV network**.
   There is no separate node, no RPC server, no relay, no indexer, no signaling server (peers on the
   decentralized network are not "a server"). The phrase "node unreachable" must never exist.
2. **EVERYTHING is on-chain. Maximize transactions.** This is BSV. Nothing is ever done off-chain.
   Starting a table is a real transaction; clicking "new game" is a transaction; **every deal is a
   transaction; every card is an NFT on-chain**; the mental-poker commitments, reveals, shuffle, and
   dealing are all real on-chain transactions; betting actions, the pot, settlement, and recovery are all
   on-chain. A single deal may be a hundred transactions; a game ~1,000; a night's play 100,000,000+.
   There are **no limits** and the goal is to **maximize** transaction count, never to reduce it.
3. **True peer-to-peer.** Full gossip network between peers; the chain is the shared source of truth.
   No central anything, ever.
4. **Real BSV only. No play money.** The wallet starts **empty** and is funded with **real BSV**; every
   value is real satoshis. The **lobby lets the user pick the network — regtest / testnet / mainnet** —
   and it is the **same code path** for all three; the network is only a parameter set, never a branch.
5. **secp256k1 only.** Ed25519 and any non-secp256k1 curve are banned. No BIP32/39/44 or any BTC-derived
   key standard, and never even name them — BSV-native keys only (one 32-byte seed → HMAC-derived keys,
   Base58Check seed backup). The FORKID sighash is BSV consensus (never call it "BIP-143").
6. **No `OP_RETURN`** is ever produced by our own scripts (card/commitment data is bound with `OP_DROP`).
7. **Always recoverable.** Before a satoshi is ever at risk, every player holds a fully pre-signed
   unilateral nLockTime refund of 100% of their funds. No satoshi can ever be stranded.
8. **Guaranteed termination.** Closing the window returns all funds and leaves zero orphan processes.
9. **Design-award quality + an app icon.** The UI must be something Steve Jobs would give a design award
   to — beautiful, coherent, effortless. The exe **must ship with an app icon**. Ugly = rejected.
10. **Military-grade engineering.** Better than MilSpec — code good enough to run on military hardware:
    SANS/CWE, NASA Power-of-Ten, MS-SDL. Validate all hostile input, never panic, fuzz every parser.
    Exhaustive WHAT/HOW/WHY docs co-equal with code. Every claim has a positive **and** a hostile-negative
    test. Red-team fixes are always **last**, never a phase trigger.

---

## 1. The BSV node (the client is one)

### 1.1 Network parameters (researched — `bitcoin-sv/src/chainparams.cpp`)

| Network | Magic (pchMessageStart) | Default port | addr ver | script ver | WIF ver |
|---------|-------------------------|--------------|----------|------------|---------|
| mainnet | `e3 e1 f3 e8`           | 8333         | 0x00     | 0x05       | 0x80    |
| testnet | `f4 e5 f3 f4`           | 18333        | 0x6f     | 0xc4       | 0xef    |
| STN     | `fb ce c4 f9`           | 9333         | 0x6f     | 0xc4       | 0xef    |
| regtest | `da b5 bf fa`           | 18444        | 0x6f     | 0xc4       | 0xef    |

(Exact testnet/STN/regtest magics to be re-verified against source during implementation.)

### 1.2 Wire protocol — full P2P message support

- **Message envelope:** `magic(4) ‖ command(12, ASCII null-padded) ‖ length(4 LE) ‖ checksum(4 = first 4
  bytes of SHA-256d(payload)) ‖ payload`.
- **Handshake:** outbound `version` → inbound `version` → exchange `verack`; honor protocol version,
  services, user-agent, start height, relay flag; `sendheaders`/`feefilter`/`protoconf` as applicable.
- **Messages (all of them):** `version, verack, ping, pong, addr, addrv2, getaddr, inv, getdata,
  notfound, tx, block, headers, getheaders, getblocks, mempool, sendheaders, feefilter, reject,
  protoconf` (BSV), plus large-message handling (`extmsg`, BSV streams) per the BSV protocol specs.
- **Peer discovery:** DNS seeds (mainnet: seed.bitcoinsv.io, seed.satoshisvision.network,
  seed.bitcoinseed.directory), `addr` gossip, persisted peer table, outbound + inbound (listening) slots.
- **Listening node:** accepts inbound peers, relays valid txs and headers, answers `getheaders`/`getdata`,
  serves its mempool, maintains the peer graph. Non-mining (no block production).

### 1.3 Chain participation, validation, and broadcast

- **The client broadcasts EVERYTHING to the network it is a peer of.** Every game action becomes a real
  transaction that the client signs and **relays into the BSV network over its own P2P connections**
  (never via an external RPC). It receives, validates, and relays others' transactions and headers.
- **Headers chain:** download and fully validate the header chain (PoW, difficulty/DAA, timestamps,
  linkage), persist it, follow the most-work chain, handle reorgs.
- **Verification:** track relevant txs via merkle-proof (`merkleblock` / BUMP / TSC proofs) verified
  against validated headers, so on-chain game state is confirmed from the chain itself. (A desktop peer
  validates headers + the proofs for the txs it cares about; it does not archive the multi-terabyte block
  history — stated plainly, never misrepresented.)
- **Script interpreter:** a BSV script engine for the scripts we create and accept (P2PKH, bare/threshold
  multisig, the card-NFT and mental-poker commitment/reveal scripts), strict and fuzzed.
- **Mempool + fee policy:** local mempool, fee estimation, relay.

---

## 2. The wallet — everything Electrum + Bitcoin Core have

Real BSV, starts empty. Feature inventory (all required):

- **Keys:** BSV-native deterministic keys from one 32-byte seed; Base58Check **seed backup** + restore;
  WIF import/export; per-instance identity; deterministic receive + change chains.
- **Receive:** address list, fresh addresses, QR codes, address labels, "used/unused" status, request
  (invoice) amounts + memos.
- **Send:** amount, fee control in sat/byte, **coin control** (a Coins/UTXO tab to view, label, freeze,
  and hand-pick UTXOs), preview, batch/multi-output sends, max/sweep, OP-safe scripts.
- **UTXO management:** UTXO set, freeze/unfreeze, labels, dust handling, consolidation.
- **Multisig:** create m-of-n wallets, co-sign workflows, an offline/air-gapped signing flow (a
  PSBT-equivalent partially-signed-tx exchange format).
- **Watch-only:** import xpub-equivalent / address set; monitor without keys.
- **History:** full transaction history with SPV proof status, confirmations, labels, fiat-optional,
  export (CSV).
- **Security:** mandatory seed encryption at rest (strong KDF), unlock throttling, no secret on the
  clipboard by default, lock-on-idle.
- **Messaging:** sign/verify messages (secp256k1, BSV address).
- **Sweep & import:** sweep a WIF, import keys, restore from seed.
- **On-chain via the node itself:** the wallet builds, signs, and the **client's own node** relays the
  transaction to the BSV network over the P2P protocol (never an external RPC).
- **Card NFTs:** all cards held as 1-sat NFTs in the wallet, sealed to the owner; transfer re-seals.

---

## 3. The six poker games (six distinct, visible, fully-playable games)

The lobby presents **six** clearly-distinct games, each visibly different and fully playable for real BSV:

1. **Texas Hold'em** — 2 hole cards, 5 community, best 5.
2. **Omaha** — 4 hole cards, use exactly 2 + 3 community.
3. **Omaha Hi-Lo (Big O / 8-or-better)** — Omaha-style hi-lo split.
4. **Seven-Card Stud** — no community board; 3 down + 4 up; best 5.
5. **Razz** — seven-card lowball.
6. **Five-Card Draw** — draw poker.

(Exact six to be confirmed with the user; each must be a real, distinct, selectable, playable game with
correct rules, hand evaluation, betting structure, and showdown — not a single engine with a hidden flag.)

Each game: dealerless **mental poker** with TRUE per-card privacy (commutative-encryption on secp256k1),
real-BSV buy-in via threshold escrow, cooperative settlement to the winner, and the always-on nLockTime
recovery. Multi-hand sessions, blinds/antes per game, side pots, correct showdown.

### 3.1 Everything on-chain (the transaction model)

Nothing is off-chain. Each of the following is a **real BSV transaction** broadcast by the client's node:

- **Create table** → a table-genesis transaction. **Start a game / new hand** → a transaction.
- **The shuffle and deal** → on-chain: each player's commitment, each mask/re-mask step, and each reveal
  is a transaction; **every card is a 1-sat NFT** (UTXO) carried and re-spent on-chain as it is dealt and
  as ownership changes; sealing/transfer is an on-chain re-spend.
- **Every betting action**, the pot escrow, the per-street board reveals, the showdown reveals, the
  settlement to the winner, and the recovery are all transactions.
- Card/commitment data is bound with `OP_DROP` (never `OP_RETURN`). The chain is the authoritative game
  state; peers verify it by SPV proof rather than trusting each other.
- **Maximize** transactions — a deal is ~100 txs, a game ~1,000, a night 100,000,000+. No batching to
  "save" transactions; volume is the point. This is BSV.

### 3.2 Lobby network selector

The lobby has a **network selector: regtest / testnet / mainnet** (same code path; only the network
parameter set + the peer set differ). The wallet and node operate on the chosen network end to end.

---

## 4. True P2P gameplay + encrypted chat

- **Gossip mesh** between players (no server); presence + table discovery serverless and signed.
- **Authenticated protocol:** every game message signed and bound to seat/hand/phase/sequence.
- **Encrypted chat:** DMs and groups, fresh ephemeral ECDH key per recipient per message (no key reuse,
  ever) → AES-256-GCM; persistent history; everything Telegram/WhatsApp do (functionally).

---

## 5. Design (Steve-Jobs bar)

- A coherent visual design system: typography scale, spacing grid, color, motion, iconography.
- A real card/table rendering that looks premium (felt, chips, cards, dealer button, animations).
- Effortless flows: open → fund (real BSV) → lobby → pick one of six games → play. Zero clutter.
- Native Windows `poker.exe`, single file, double-click, no options, no install.
- Verified to actually render (screenshot/UIA), never "process alive" reported as working.

---

## 6. Engineering & acceptance

- Pure .NET, pure in-tree crypto (secp256k1, hashes, AEAD), no banned dependencies.
- Single-file self-contained `poker.exe`; CI builds + runs the full test suite on Windows.
- Every behavioural claim: positive + hostile-negative test. Parsers fuzzed.
- Exhaustive docs (architecture, node, wallet, games, P2P, on-chain, security, build).
- **Acceptance is binary.** Missing or ugly = rejected.

---

## 7. Build order (each stage 100% + tested before the next)

1. Crypto foundation (secp256k1, hashes, Base58Check, AEAD) — in-tree, verified vs vectors.
2. **BSV node**: wire protocol + handshake + peer discovery + headers sync + SPV proofs + mempool/relay.
3. **Wallet**: real-BSV, the full feature inventory in §2, funded from the node's own network view.
4. **Six games**: engines + hand eval + dealerless EC mental poker + real-BSV escrow/settlement/recovery.
5. **P2P gameplay + chat**: authenticated gossip, encrypted DubM/group chat.
6. **Design pass**: the Steve-Jobs-grade UI across every surface.
7. Red-team hardening — **last**.
