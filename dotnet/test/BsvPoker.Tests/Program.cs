using BsvPoker.Tests;

Console.WriteLine("=== BsvPoker .NET test suite ===\n");

Secp256k1Tests.All();
CryptoPrimitivesTests.All();
GameTests.All();
PokerEvalTests.All();
LowEvalTests.All();
GameShowdownTests.All();
GameOnChainTests.All();
OnChainHandTests.All();
OnChainSessionTests.All();
OnChainHandTapeTests.All();
BlackjackTests.All();
TxTemplatesTests.All();
MentalPokerECTests.All();
VariantTests.All();
ChainTests.All();
EscrowTests.All();
WalletKeysTests.All();
WalletExtrasTests.All();
OnChainWalletTests.All();
CardNftTests.All();
OnChainChatTests.All();
TxLinkTests.All();
SecurityHardeningTests.All();
TwoPartyEscrowTests.All();
ShuffleProofTests.All();
HandTranscriptTests.All();
SeatOrderTests.All();
VerifiedHandTests.All();
LiveDealTests.All();
LiveHandTests.All();
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
NetGameTests.All();   // RESTORED: the real multi-hand multiway dealerless mental-poker session engine
NetGameStressTests.All();   // 100x open/play/close acceptance bar

return T.Summary();
