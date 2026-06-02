# User guide

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

## Variants
Texas Hold'em (Phase 1), plus Omaha, Seven-Card Stud, Five-Card Draw, and Razz as game modules.
Blackjack is a separate, later track (its dealerless model differs from the poker shuffle).

## Your keys
Your keys live behind a custody boundary and never reach the page. In the default software custody
this trusts your device — stated plainly. Threshold custody (no whole key in one place) and a
hardware-enclave backend are optional later upgrades.
