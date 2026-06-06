using BsvPoker.Tests;

Console.WriteLine("=== BsvPoker .NET test suite ===\n");

Secp256k1Tests.All();
CryptoPrimitivesTests.All();
GameTests.All();
MentalPokerECTests.All();
VariantTests.All();
ChainTests.All();
EscrowTests.All();
WalletKeysTests.All();
WalletExtrasTests.All();
CardNftTests.All();
NetTests.All();
NetGameTests.All();
NetChatTests.All();
BsvProtocolTests.All();
BsvHeadersTests.All();
ScriptTests.All();

return T.Summary();
