using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>
/// Hostile-input hardening (from the production-security audit): point/scalar validation, mental-poker
/// shuffle/remask/unmask input rejection, transaction-parser hard caps, recovery/settlement value bounds,
/// and WIF/seed-decrypt validation. Each is a positive + a hostile-negative claim.
/// </summary>
public static class SecurityHardeningTests
{
    public static void All()
    {
        Console.WriteLine("security hardening (hostile-input rejection):");

        T.Run("IsValidPoint / IsValidScalar accept good and reject bad", () =>
        {
            var k = Secp256k1.GenerateKeyPair();
            T.True(Secp256k1.IsValidPoint(k.Pub), "real pubkey is a valid point");
            T.False(Secp256k1.IsValidPoint(new byte[33]), "all-zero is not a point");
            T.False(Secp256k1.IsValidPoint(new byte[] { 0x02 }.Concat(new byte[32]).ToArray()), "x=0 not on curve");
            T.True(Secp256k1.IsValidScalar(k.Priv), "real privkey is a valid scalar");
            T.False(Secp256k1.IsValidScalar(new byte[32]), "zero scalar rejected");
            T.False(Secp256k1.IsValidScalar(Enumerable.Repeat((byte)0xff, 32).ToArray()), "scalar >= n rejected");
        });

        T.Run("ShuffleMask rejects a non-permutation and an invalid global scalar", () =>
        {
            var deck = MentalPokerEC.BaseDeck(9);
            var g = MentalPokerEC.NewScalar();
            T.Throws(() => MentalPokerEC.ShuffleMask(deck, g, new[] { 0, 0, 2, 3, 4, 5, 6, 7, 8 }), "duplicate index rejected");
            T.Throws(() => MentalPokerEC.ShuffleMask(deck, g, new[] { 9, 1, 2, 3, 4, 5, 6, 7, 8 }), "out-of-range index rejected");
            T.Throws(() => MentalPokerEC.ShuffleMask(deck, new byte[32], new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }), "zero global scalar rejected");
        });

        T.Run("ValidateDeck / Remask / Unmask reject malformed points and scalars", () =>
        {
            var deck = MentalPokerEC.BaseDeck(4);
            var bad = (byte[][])deck.Clone(); bad[1] = new byte[33]; // an invalid point
            T.Throws(() => MentalPokerEC.ValidateDeck(bad), "invalid point in deck rejected");
            T.Throws(() => MentalPokerEC.Remask(deck, MentalPokerEC.NewScalar(), new[] { MentalPokerEC.NewScalar(), new byte[32], MentalPokerEC.NewScalar(), MentalPokerEC.NewScalar() }), "invalid per-card scalar rejected");
            T.Throws(() => MentalPokerEC.Unmask(new byte[33], new[] { MentalPokerEC.NewScalar() }), "invalid card point rejected");
            T.Throws(() => MentalPokerEC.Unmask(deck[0], new[] { new byte[32] }), "invalid scalar share rejected");
        });

        T.Run("MentalPoker.Compose rejects a non-permutation", () =>
        {
            T.Throws(() => MentalPoker.Compose(new[] { new[] { 0, 0, 1 } }, 3), "duplicate index in a party permutation rejected");
        });

        T.Run("tx deserializer rejects an absurd input count (fuzz hard cap)", () =>
        {
            // version(4) ‖ VarInt nIn = 0xff + 0xffffffffffffffff
            var bytes = new byte[] { 1, 0, 0, 0, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
            T.Throws(() => Chain.Deserialize(bytes), "huge input count is capped, not allocated");
        });

        T.Run("BuildRecovery rejects fee >= amount (no negative-output recovery)", () =>
        {
            var k = WalletKeys.Account(WalletKeys.NewSeed(), 0, 0);
            T.Throws(() => Chain.BuildRecovery("aa".PadRight(64, '1'), 0, 1000, k.Priv, k.Pub, 1000, 900_000), "fee == amount rejected");
            T.Throws(() => Chain.BuildRecovery("aa".PadRight(64, '1'), 0, 1000, k.Priv, k.Pub, -1, 900_000), "negative fee rejected");
        });

        T.Run("BuildSplitSettlement cannot create money (payouts ≤ escrow)", () =>
        {
            var a = Secp256k1.GenerateKeyPair(); var b = Secp256k1.GenerateKeyPair();
            T.Throws(() => Chain.BuildSplitSettlement("bb".PadRight(64, '2'), 0,
                new (byte[], long)[] { (a.Pub, 60000), (b.Pub, 60000) }, escrowAmount: 100000), "payouts exceeding escrow rejected");
            // within escrow is fine
            var ok = Chain.BuildSplitSettlement("bb".PadRight(64, '2'), 0, new (byte[], long)[] { (a.Pub, 99000) }, 100000);
            T.Eq(ok.Outs.Count, 1, "valid split builds");
        });

        T.Run("FromWif validates version, compression flag, and scalar; DecryptSeed bounds iterations", () =>
        {
            var k = WalletKeys.Account(WalletKeys.NewSeed(), 0, 0);
            var wif = WalletExtras.ToWif(k.Priv);
            T.Eq(T.Hex(WalletExtras.FromWif(wif).Priv), T.Hex(k.Priv), "round-trips");
            T.Throws(() => WalletExtras.FromWif(wif, version: 0x6f), "wrong version byte rejected");
            // a forged enc1 blob with an absurd iteration count is rejected before doing the work
            T.Throws(() => WalletExtras.DecryptSeed("enc1.99999999.AAAAAAAAAAAAAAAAAAAAAA==.AAAA", "pw"), "out-of-bounds iteration count rejected");
        });
    }
}
