using BsvPoker.Core;

namespace BsvPoker.Tests;

/// <summary>
/// Verifiable shuffle/remask correctness (audit-critical #1): an honest shuffle/remask verifies against its
/// commitment, and any cheating — a substituted, dropped, or duplicated card, a wrong permutation, or a
/// commitment that doesn't open — is DETECTED. This closes the "signed malicious points enter the protocol"
/// hole: a cheat is provable at reveal/showdown.
/// </summary>
public static class ShuffleProofTests
{
    public static void All()
    {
        Console.WriteLine("verifiable shuffle/remask proof (cheating is detected):");
        int n = 9;

        T.Run("an honest shuffle verifies against its commitment", () =>
        {
            var input = MentalPokerEC.BaseDeck(n);
            var g = MentalPokerEC.NewScalar();
            var perm = Perm(n, 3);
            var commit = ShuffleProof.CommitShuffle(g, perm);
            var output = MentalPokerEC.ShuffleMask(input, g, perm);
            T.True(ShuffleProof.VerifyShuffle(input, output, g, perm, commit), "honest shuffle verifies");
        });

        T.Run("a SUBSTITUTED card is detected", () =>
        {
            var input = MentalPokerEC.BaseDeck(n);
            var g = MentalPokerEC.NewScalar(); var perm = Perm(n, 5);
            var commit = ShuffleProof.CommitShuffle(g, perm);
            var output = MentalPokerEC.ShuffleMask(input, g, perm);
            output[2] = MentalPokerEC.BaseDeck(n)[0]; // swap in an unrelated point
            T.False(ShuffleProof.VerifyShuffle(input, output, g, perm, commit), "substitution caught");
        });

        T.Run("a DROPPED/DUPLICATED card is detected", () =>
        {
            var input = MentalPokerEC.BaseDeck(n);
            var g = MentalPokerEC.NewScalar(); var perm = Perm(n, 7);
            var commit = ShuffleProof.CommitShuffle(g, perm);
            var output = MentalPokerEC.ShuffleMask(input, g, perm);
            output[4] = output[3]; // duplicate one card, dropping another
            T.False(ShuffleProof.VerifyShuffle(input, output, g, perm, commit), "duplication/drop caught");
        });

        T.Run("a LIED permutation (doesn't match the commitment) is detected", () =>
        {
            var input = MentalPokerEC.BaseDeck(n);
            var g = MentalPokerEC.NewScalar(); var perm = Perm(n, 11);
            var commit = ShuffleProof.CommitShuffle(g, perm);
            var output = MentalPokerEC.ShuffleMask(input, g, perm);
            var otherPerm = Perm(n, 99); // a different permutation than committed
            T.False(ShuffleProof.VerifyShuffle(input, output, g, otherPerm, commit), "wrong perm fails the commitment/recompute");
        });

        T.Run("an honest remask verifies; a tampered remask output is detected", () =>
        {
            var input = MentalPokerEC.BaseDeck(n);
            var g = MentalPokerEC.NewScalar();
            var perCard = MentalPokerEC.NewPerCardScalars(n);
            var commit = ShuffleProof.CommitRemask(g, perCard);
            var output = MentalPokerEC.Remask(input, g, perCard);
            T.True(ShuffleProof.VerifyRemask(input, output, g, perCard, commit), "honest remask verifies");
            output[1] = output[0]; // tamper
            T.False(ShuffleProof.VerifyRemask(input, output, g, perCard, commit), "tampered remask caught");
        });

        T.Run("ValidateDeck rejects a deck with duplicate points", () =>
        {
            var deck = MentalPokerEC.BaseDeck(n);
            deck[5] = deck[4];
            T.False(MentalPokerEC.IsWellFormedDeck(deck), "duplicate point in deck rejected");
        });
    }

    private static int[] Perm(int n, int salt)
    {
        var a = Enumerable.Range(0, n).ToArray();
        var rnd = new Random(salt);
        for (int i = n - 1; i > 0; i--) { int j = rnd.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
        return a;
    }
}
