# Build Backlog — the real distance (hundreds of stages, thousands of steps)

Honest framing: this is **stage ~0–1 of hundreds**. `[x]` = built **and verified**; `[~]` = library exists
but **not integrated/verified end-to-end**; `[ ]` = not started. The vast majority is `[ ]`. This is the
"100% red test" as an actionable backlog — not a claim of completion.

## Stage 0 — crypto & primitives
- [x] secp256k1 (ECDSA RFC6979 low-S, ECDH, point ops), hashes, AEAD, Base58Check, BSV-native keys

## Stage 1 — BSV node (be a real node on the network)
- [x] P2P wire envelope + version/verack handshake
- [x] Connect to LIVE mainnet/testnet seeds + read chain tip (verified: mainnet 952,433 / testnet 1,739,771)
- [x] Download + VALIDATE real headers from LIVE peers (getheaders/headers protocol works on the real chain)
- [x] MULTI-BATCH header sync from genesis (locator advances, cross-batch PoW+linkage): verified 24,000 (testnet) + 16,000 (mainnet) headers downloaded & validated
- [~] header chain lib (parse/PoW/reorg) — [ ] sync ALL the way to the chain TIP (millions of headers) + PERSISTENT store + checkpoints + ongoing follow
- [ ] persistent header store; checkpoints; difficulty-retarget (DAA) enforcement
- [ ] addr/getaddr peer management, peer scoring, ban list, reconnection, persistence
- [ ] inv/getdata/tx/block relay verified on the live network; mempool; feefilter; large-message/streams
- [ ] broadcast a real tx that gets MINED (verified inclusion via SPV)
- [ ] run as an inbound LISTENING node reachable from the internet (NAT traversal / UPnP / port guidance)

## Stage 2 — real-BSV wallet (everything Electrum + Bitcoin Core have)
- [~] coin selection + signed spend + change (lib); [~] SPV-funding verifier (synthetic) ; [~] 2-of-2 escrow/settle/recover (lib)
- [ ] RECEIVE real BSV; live balance/UTXO set from the chain; confirmations via SPV
- [ ] coin-control UI (view/label/freeze/select UTXOs); dust handling; consolidation
- [ ] multisig m-of-n wallets + co-sign UI; watch-only; PSBT-equivalent air-gapped flow
- [ ] labels, address book/contacts, request/invoice with memos, QR codes
- [ ] fee policy (sat/byte, replace/bump-equivalent), batch/multi-output sends, sweep/import WIF
- [ ] full transaction HISTORY with SPV proof status + export; fiat-optional
- [ ] mandatory seed-at-rest encryption, unlock throttle, no-clipboard-secrets, lock-on-idle, memory hygiene

## Stage 3 — Bitcoin Script smart-contract platform ("everything Ethereum has, on BSV")
- [~] Script interpreter (subset) + a few templates (P2PKH/hash-lock/time-lock/one auction example)
- [ ] complete opcode set + strict consensus rules; full fuzzing of the interpreter
- [ ] full per-type contracts for ALL 21+ transaction templates (today: tags+field-names only)
- [ ] auctions: English, Dutch, sealed-bid; EVERY role auctioned; every bid a full conditional contract
- [ ] payment channels, threshold/oracle conditions, state contracts; a contract authoring/verify toolkit

## Stage 4 — on-chain mental poker & dealing
- [~] commutative-encryption deal MATH (lib, off-chain)
- [ ] each commitment/mask/shuffle-stage/deal/board/showdown as a TYPED on-chain transaction, verifiable
- [ ] cards as on-chain NFTs minted/dealt/transferred on-chain; provable fairness audit trail

## Stage 5 — the games (each fully playable, on-chain, multiplayer, real money)
- [~] high/low evaluators + per-game showdown (lib); [~] off-chain heads-up Hold'em
- [ ] full multi-street betting engines per game wired to on-chain typed txs + escrow + private deal:
  Texas Hold'em, Omaha, Omaha Hi-Lo, Seven-Card Stud, Razz, Five-Card Draw, Blackjack
- [ ] N-player tables (3..max), tournaments, blinds/antes schedules, sit-out/rejoin, multi-table

## Stage 6 — P2P gameplay, transport, discovery, chat
- [~] gossip mesh + local discovery + encrypted chat (lib/app, off-chain)
- [ ] fully AUTHENTICATED transport (every message signed + transcript-bound); signed presence/tables
- [ ] internet play between strangers (discovery without a server; NAT traversal); reconnection/resume
- [ ] chat as typed on-chain transactions (DM + groups), contact identity, no key reuse — verified

## Stage 7 — UX / design (Steve-Jobs bar)
- [ ] a real visual design system (type, spacing, color, motion, icon set); premium table/cards/chips
- [ ] effortless flows (open → fund real BSV → lobby → pick a game → play → cash out); every screen polished
- [ ] verified rendering (screenshots), accessibility, responsiveness

## Stage 8 — security / red-team (LAST)
- [ ] full hostile-environment test matrix; fuzz every parser; transcript-bound auth everywhere
- [ ] seat-grinding mitigation; rate/DoS hardening by byte cost; abort/forfeit accountability end-to-end
- [ ] independent audit; every claim a positive + hostile-negative test

## Stage 9 — ops / release
- [ ] code-signed single-file exe; clean-machine launch matrix (100+ runs); reproducible builds
- [ ] CI tied to a real on-chain smoke test (a real hand on testnet); release evidence + hashes
- [ ] exhaustive published docs per subsystem

## Stages 10..hundreds — the rest of the vision
- [ ] everything that exists on any blockchain / Ethereum, rebuilt in BSV Script, as its own stage.

**Honest completion vs all of the above: a few percent.** Do not call it done until real players fund real
wallets and play real-money hands on mainnet, end-to-end, verified.
