# BSV Poker

A **dealerless, peer-to-peer poker client for BSV**, delivered as a single native Windows
executable (`poker.exe`). No server, no installer, no separate DLLs, no runtime prerequisite —
double-click and play. Pure C# / .NET, pure **secp256k1** (the BSV curve), BSV-native throughout.

> Status: actively built. The app runs with a real wallet, encrypted chat, a peer-to-peer lobby,
> six poker variants, and cards held as NFTs in your wallet. See [Roadmap](#roadmap) for what is
> still being hardened. This README describes what is actually implemented today.

---

## What it is

`poker.exe` is one window with four tabs:

| Tab | What it does |
|-----|--------------|
| **Wallet** | A BSV-native deterministic wallet: one 32-byte master **seed** backs up everything; spending keys derive directly from it; receive addresses, send, transaction history, seed backup/restore, WIF export, signed messages, and optional password-at-rest (AES-256-GCM). Cards you hold appear here as NFTs. |
| **Lobby** | Fully peer-to-peer (no server). Host a table for any of the six variants, connect to another player by their `IP:port`, see tables appear on the gossip mesh, join one, or **play a bot**. |
| **Game** | The poker table: practice, vs-bot, and **networked multiplayer (2–6 players)** over the P2P mesh, with **true hole-card privacy** (commutative-encryption deal — you see only your own cards until showdown). Texas Hold'em engine with blinds, all-in side pots, showdown, and chip conservation; six variants. |
| **Chat** | Encrypted messaging — direct messages and groups. Every message uses a **fresh ephemeral ECDH key per recipient** (no key reuse, ever) → HKDF → AES-256-GCM. The wire is ciphertext. History persists across restarts. |

Two copies of `poker.exe` on the same machine are two **different players** — each instance claims
its own profile directory (exclusive file lock) with its own wallet and identity.

## Design rules (non-negotiable)

- **Pure peer-to-peer.** There is no relay, indexer, or any central server in any code path.
- **BSV-native.** secp256k1 only. The wallet is one master seed → HMAC-derived keys, backed up as a
  single Base58Check seed string. The on-chain sighash is the BSV FORKID sighash.
- **secp256k1 only** — Ed25519 is not used anywhere.
- **No `OP_RETURN`.** Card NFTs bind their data with `OP_DROP`, never `OP_RETURN`.
- **Same model on every network.** regtest / testnet / mainnet are one code path; the network is only
  a config tag plus a node endpoint, never a branch.
- **Always recoverable.** A player holds a pre-signed unilateral nLockTime refund of their funds
  before risking a satoshi (see [docs/ONCHAIN_MODEL.md](docs/ONCHAIN_MODEL.md)).
- **Clean termination.** Closing the window always returns funds and leaves no orphan processes.

## Repository layout

```
dotnet/
  BsvPoker.sln
  src/BsvPoker.Crypto/   secp256k1, RIPEMD-160/HASH160/SHA-256d, Base58Check, AES-256-GCM + HKDF
  src/BsvPoker.Core/     cards, dealerless mental poker, hand eval, Hold'em engine + 6 variants,
                         BSV-native wallet keys, BSV transactions + FORKID sighash + nLockTime
                         recovery, card NFTs, wallet extras (WIF/signed-msg/seed encryption), bot
  src/BsvPoker.Net/      P2P gossip mesh, networked N-player game protocol, encrypted chat
  src/BsvPoker.App/      WPF app (AssemblyName=poker): tabs, per-instance profile, card vault, views
  test/BsvPoker.Tests/   dependency-free console test runner (no test framework)
docs/                    architecture and subsystem documentation
.github/workflows/       CI (build + test on Windows) and the release publish
```

## Build and run

The .NET 8 SDK is required. WPF means the app builds and runs on **Windows**.

```powershell
# from the repo root
dotnet build dotnet/BsvPoker.sln -c Release

# run the test suite (a non-zero exit code fails)
dotnet run --project dotnet/test/BsvPoker.Tests/BsvPoker.Tests.csproj -c Release

# produce the single-file, self-contained poker.exe
dotnet publish dotnet/src/BsvPoker.App/BsvPoker.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

The published `dist/poker.exe` is everything — just run it.

See [docs/BUILD_AND_TEST.md](docs/BUILD_AND_TEST.md) for details.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the projects, their boundaries, and how a hand flows.
- [docs/WALLET_AND_KEYS.md](docs/WALLET_AND_KEYS.md) — the BSV-native seed/key model and backup.
- [docs/MENTAL_POKER.md](docs/MENTAL_POKER.md) — the dealerless shuffle and card NFTs.
- [docs/P2P_AND_CHAT.md](docs/P2P_AND_CHAT.md) — the gossip mesh, game protocol, and encrypted chat.
- [docs/ONCHAIN_MODEL.md](docs/ONCHAIN_MODEL.md) — BSV transactions, the FORKID sighash, recovery.
- [docs/SECURITY.md](docs/SECURITY.md) — the threat model and security properties.
- [docs/BUILD_AND_TEST.md](docs/BUILD_AND_TEST.md) — building, testing, and releasing.

## Implemented since the first cut

- **True cryptographic hole-card privacy** (commutative-encryption mental poker, secp256k1-only), proven
  2-player and 3-player.
- **Networked multiplayer up to 6 players**, with multiway side pots.
- **On-chain 2-of-2 escrow** with cooperative settlement and a co-signed nLockTime recovery (strict,
  consensus-grade verification), built and tested in-process.

## Roadmap

- Funding coins onto the chain is **external** to this standalone product (a separate testnet-funding
  node, used only for testing) — the app and wallet themselves never connect to any node or server.
- **Transport authentication** — signing every protocol message and the directory/presence; today the
  mesh is unauthenticated, so networked play is privacy-preserving but not yet safe against an active
  hostile peer. This is part of the deferred red-team hardening. See [docs/SECURITY.md](docs/SECURITY.md).

## License

See repository.
