using BsvPoker.Tests;

Console.WriteLine("=== BsvPoker .NET test suite ===\n");

Secp256k1Tests.All();
CryptoPrimitivesTests.All();
ThresholdSharingTests.All();
ThresholdEcdsaTests.All();
ThresholdCustodyTests.All();
StealthDistributionTests.All();
OnChainThresholdHandTests.All();
DistributedKeyGenTests.All();
DistributedSignTests.All();
MeshDkgTests.All();
MeshSignTests.All();
OnChainBettingTests.All();
OnChainNofNPotTests.All();
ZeroConfMoveChainTests.All();   // zero-conf resilient move-chain: every move n-of-n, no double-spend at 0-conf
BotStakeTests.All();   // D-C: 1-of-2 bot stake — owner always reclaims, bot plays with it
MultiwayHandTests.All();   // multiway dealerless on-chain hand: n-of-n privacy + zero-conf move-tape + game-bound NFTs
GamePointsTests.All();   // D-A/D-B: points permanently linked to identity + game
RedundantMoveBroadcastTests.All();   // redundant dual-path: every move IP-to-IP to all peers AND to nodes
ReclamationCovenantTests.All();      // EP4152683B1 end-of-game reclamation covenant (holding key + time-lock)
StealthConveyanceTests.All();        // stealth point P conveyed via typed PUSHDATA (no OP_RETURN), end-to-end
BroadcastEnvelopeRevocationTests.All(); // broadcast encryption: arbitrary set (3/5/7/200) + revocation, hostile
HandEvalEdgeTests.All();             // hand-eval edges: ties/odd-chip, side pots, Omaha-8 low, razz/wheel
MoneyStateTests.All();               // SPV three-state money: Confirmed / Unconfirmed / DoubleSpendOrInvalid
KeyModelTests.All();                 // single-use keys, identity-only-persistent, hash chain, network-independent
CryptoHardeningTests.All();          // hostile-edge hardening of secp256k1 / AEAD / hashes / Base58
VariantFlowTests.All();              // per-variant street/board counts, conservation, side pots, hostile actions
FullGameplayTests.All();             // FULL hands to showdown, all variants/2-6p, chips conserved, correct winner
ReplayDemoTests.All();               // REPLAY: on-chain hand tape loads, every move parses, ends in settlement
NodeSeedRegistryTests.All();         // on-chain node-seed registry: well-known address, IP:port + expiry, no OP_RETURN
DiscoveryTests.All();                // automatic TCP peer discovery: two nodes auto-connect, tables become visible
MultiPlayerGameTests.All();          // FIVE distinct-wallet players join ONE table, private deal, full hand, chips conserved
SettlementMoveTests.All();           // on-chain n-of-n pot payout to winner(s); conserves value; hostile-proof
IdentityLifecycleTests.All();        // on-chain identity NFT: build/parse/verify, permanence, uniqueness, hostile
NewWalletFlowTests.All();            // 100x: new wallet FUND → register identity ON-CHAIN (never free) → permanent → play
RegtestFundFlowTests.All();          // 100x: REGTEST self-fund (real PoW+SPV) → register ON-CHAIN → permanent → play
GameTests.All();
PokerEvalTests.All();
LowEvalTests.All();
GameShowdownTests.All();
GameOnChainTests.All();
OnChainHandTests.All();
OnChainSessionTests.All();
OnChainHandTapeTests.All();
BlackjackTests.All();
GroupBlackjackTests.All();   // multiplayer group Blackjack: N players, one communal dealer, shared pot conserved
BroadcastEncryptionTests.All();
BroadcastEnvelopeTests.All();
TxTemplatesTests.All();
MentalPokerECTests.All();
VariantTests.All();
ChainTests.All();
EscrowTests.All();
WalletKeysTests.All();
WalletExtrasTests.All();
OnChainWalletTests.All();
CardNftTests.All();
GameNftBindingTests.All();   // D-A: a card NFT is permanently bound to its game; cannot cross games
OnChainChatTests.All();
ChatDeliveryTests.All();   // a chat message is actually pushed IP-to-IP and ARRIVES (loopback-bind regression)
TxLinkTests.All();
SecurityHardeningTests.All();
TwoPartyEscrowTests.All();
ShuffleProofTests.All();
RevealProofTests.All();
HandTranscriptTests.All();
SeatOrderTests.All();
VerifiedHandTests.All();
SpvEnvelopeTests.All();
PokerGossipTests.All();
BsvProtocolTests.All();
BsvHeadersTests.All();
HeaderStoreTests.All();
MerkleProofTests.All();
PartialMerkleTreeTests.All();
TxCodecTests.All();
SpvFundingTests.All();
IdentityTests.All();
BloomFilterTests.All();
SpvDiscoveryTests.All();
ScriptTests.All();
OnChainIdentityTests.All();
NetGameJoinTests.All();   // host-already-hosting still pairs up + game STARTS (hello-dedup regression) + who's-online directory
NetGameTests.All();   // RESTORED: the real multi-hand multiway dealerless mental-poker session engine
NetGameStressTests.All();   // 100x open/play/close acceptance bar

return T.Summary();
