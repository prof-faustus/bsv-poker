# User guide

> **⚠ Peer-to-peer update.** bsv-poker is now **fully peer-to-peer** — there is **NO central relay or
> indexer server** (`apps/relay-go` and `apps/indexer-go` are deleted). Where this document still says
> "relay"/"indexer", read it as the gossip transport (`packages/adapters/src/p2p-transport.ts`) and the
> player's own local node (`tools/local-node.ts`): the SAME signed envelope protocol carried directly
> between peers, with discovery as a gossiped directory. The same model runs on regtest, testnet, and
> real BSV. Authoritative description: [`P2P_MODEL.md`](../P2P_MODEL.md).

> **Regtest / play-money.** This is research software. By default it uses a local regtest chain
> and play-money chips with **no external monetary value**. Mainnet is reachable only behind an
> explicit research flag, and the app shows an unmissable banner when it is active. This is not a
> regulated gambling product.

## What it is
A dealerless, non-custodial multiplayer poker platform on Bitcoin SV: no server holds the deck or
decides outcomes; every game event is a signed transaction; the table is a deterministic function
of the valid transaction set. You can **fold without revealing your cards**, and no absent or
malicious player can freeze the table — every decision has a timeout default.

## Playing (web client)
1. Open the web app. You'll see the **regtest** banner — that's expected.
2. **Lobby:** create a table (variant, blinds, stacks) or join one. Joining locks your stake; if
   the table aborts before play it refunds via a pre-signed path.
3. **Table:** you'll see the seats, the board, the pot(s), the timer, and — importantly — the
   **consequence of doing nothing** ("If you do nothing, you check in 30s" / "…you fold in 30s —
   you are never forced to wager").
4. **Act:** check / bet / call / raise / fold. The app only offers legal moves and only legal bet
   sizes. Every action raises a **signing prompt** that states exactly what you are signing — there
   is no silent signing.
5. **Your cards** are decrypted locally through the custody boundary and shown only to you.
6. **Showdown:** only contenders reveal, and only what is needed to decide the pot. Settlement is
   deterministic; final balances are shown.
7. You can **export the transcript** and replay the hand offline — it reconstructs byte-identically.

## Choosing a game, funds, and multiplayer
- **Pick a variant** when you create a table: Texas Hold'em, Omaha (incl. Hi-Lo), Seven-Card
  Stud, Five-Card Draw, or Razz — and the seat count (2 up to the variant's max). Blackjack is a
  separate later track (its dealerless model differs from the poker shuffle).
- **Wallet — add and remove funds.** Your wallet shows a balance; **Add funds** credits it
  (regtest/play-money now; a real on-chain deposit is the live path), and **Withdraw** removes
  funds to an address. Joining a table **buys in** for the table's starting stack from your
  balance; leaving **cashes out** the remaining stack back to your wallet. Amounts are whole
  satoshis (no fractional outputs).
- **Multiplayer rooms.** Connect to a relay, see the **lobby** of open tables, **create** one
  (others join from the list) or **join** an existing one. A **waiting room** shows players
  filling the seats; when the table is full everyone is seated and the hand begins — real
  player-vs-player mental poker over the relay. (There's also an offline practice-vs-bot mode.)

## Your keys
Your keys live behind a custody boundary and never reach the page. In the default software custody
this trusts your device — stated plainly. Threshold custody (no whole key in one place) and a
hardware-enclave backend are optional later upgrades.
