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
NetTests.All();
NetGameTests.All();
NetChatTests.All();
BsvProtocolTests.All();
BsvHeadersTests.All();
MerkleProofTests.All();
SpvFundingTests.All();
ScriptTests.All();

return T.Summary();
