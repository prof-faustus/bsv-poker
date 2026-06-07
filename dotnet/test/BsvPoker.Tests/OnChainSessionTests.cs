using BsvPoker.Core;
using BsvPoker.Core.Games;
using BsvPoker.Crypto;

namespace BsvPoker.Tests;

/// <summary>A composed on-chain hand: fund escrow → deal → showdown → settle the winner, with recovery ready.</summary>
public static class OnChainSessionTests
{
    private static Card C(string s)
    {
        int rank = s[0] switch { 'A' => 14, 'K' => 13, 'Q' => 12, 'J' => 11, 'T' => 10, _ => s[0] - '0' };
        var suit = s[1] switch { 's' => Suit.Spades, 'h' => Suit.Hearts, 'd' => Suit.Diamonds, _ => Suit.Clubs };
        return new Card(rank, suit);
    }

    public static void All()
    {
        Console.WriteLine("on-chain hand orchestration (fund → deal → settle):");
        T.Run("a full heads-up hand funds, decides the winner, and settles on-chain", () =>
        {
            var a = Secp256k1.GenerateKeyPair(); var b = Secp256k1.GenerateKeyPair();
            var w = new OnChainWallet(WalletKeys.NewSeed());
            w.Add(new OnChainWallet.Utxo("aa".PadRight(64, '1'), 0, 60000, 0, 0));
            // seat0 = AA, seat1 = 2-3, board gives seat0 trip aces
            var deck = new[] { C("As"), C("Ah"), C("2c"), C("3d"), C("Ad"), C("Kh"), C("Qs"), C("Jc"), C("9h") };
            var r = OnChainGameSession.PlayHoldem(w, a, b, deck, pot: 40000, fee: 500);
            T.Eq(r.WinnerSeat, 0, "seat 0 (trip aces) wins");
            T.True(w.VerifySpend(r.Funding), "escrow funding signs + conserves");
            T.True(Chain.VerifyMultisig2of2(r.Settlement, 0, a.Pub, b.Pub, 40000), "settlement co-signed + valid");
            T.Eq(T.Hex(r.Settlement.Outs[0].Script), T.Hex(Chain.P2pkhLockForPub(a.Pub)), "pot paid to the winner");
            T.True(Chain.VerifyMultisig2of2(r.Recovery, 0, a.Pub, b.Pub, 40000), "recovery is ready (always recoverable)");
        });
    }
}
