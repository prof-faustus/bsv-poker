using BsvPoker.Core;
using BsvPoker.Core.Games;

namespace BsvPoker.Tests;

/// <summary>Each of the six poker games evaluates distinctly: Hold'em, Omaha (exactly-two), Omaha Hi-Lo
/// split, Seven-Card Stud, Razz (low), Five-Card Draw. Proves the games are real and different.</summary>
public static class GameShowdownTests
{
    private static Card C(string s)
    {
        int rank = s[0] switch { 'A' => 14, 'K' => 13, 'Q' => 12, 'J' => 11, 'T' => 10, _ => s[0] - '0' };
        var suit = s[1] switch { 's' => Suit.Spades, 'h' => Suit.Hearts, 'd' => Suit.Diamonds, _ => Suit.Clubs };
        return new Card(rank, suit);
    }
    private static Card[] H(params string[] cs) => cs.Select(C).ToArray();
    private static readonly Card[] None = Array.Empty<Card>();

    public static void All()
    {
        Console.WriteLine("per-game showdown (six distinct games):");

        T.Run("six distinct games are registered with their own rules", () =>
        {
            T.Eq(PokerGames.All.Count, 6, "six games");
            T.Eq(PokerGames.Of(PokerGame.Omaha).ExactlyHole, 2, "Omaha uses exactly two hole cards");
            T.True(PokerGames.Of(PokerGame.OmahaHiLo).HiLo, "Omaha Hi-Lo splits");
            T.True(PokerGames.Of(PokerGame.Razz).LowOnly, "Razz is low-only");
            T.True(PokerGames.Of(PokerGame.FiveCardDraw).Board == 0, "draw has no board");
        });

        T.Run("Omaha's exactly-two rule prevents a board flush that Hold'em would allow", () =>
        {
            var board = H("Ah", "Kh", "Qh", "Th", "9c");
            var hole = H("Jh", "2s", "3s", "4s"); // only one heart in hand
            long holdem = Showdown.BestHigh(PokerGames.Of(PokerGame.TexasHoldem), hole, board); // royal flush (1 hole + 4 board)
            long omaha = Showdown.BestHigh(PokerGames.Of(PokerGame.Omaha), hole, board);        // can't: needs 2 hole hearts
            T.True(omaha < holdem, "Omaha cannot make the heart royal with one hole heart");
        });

        T.Run("Hold'em: the stronger seven-card hand wins the pot", () =>
        {
            var board = H("Ah", "Kd", "7s", "2c", "9h");
            var holes = new IReadOnlyList<Card>[] { H("As", "Ad"), H("Kh", "Qc") }; // trips aces vs pair kings
            var pay = Showdown.Settle(PokerGames.Of(PokerGame.TexasHoldem), holes, board, 100);
            T.Eq(pay.GetValueOrDefault(0), 100L, "trip aces win"); T.Eq(pay.GetValueOrDefault(1), 0L);
        });

        T.Run("Omaha Hi-Lo splits the pot: one seat takes high, another takes the low half", () =>
        {
            var board = H("2h", "3d", "7s", "Kc", "Qh"); // three low cards (2,3,7), no straight draw
            var holes = new IReadOnlyList<Card>[]
            {
                H("Kh", "Kd", "9s", "9c"),  // high: trip kings; both holes > 8 ⇒ no qualifying low
                H("Ah", "5d", "6s", "8c"),  // low: A,5 + 2,3,7 ⇒ 7-low (qualifies); only a high card for high
            };
            var pay = Showdown.Settle(PokerGames.Of(PokerGame.OmahaHiLo), holes, board, 100);
            T.Eq(pay.GetValueOrDefault(0), 50L, "seat 0 wins the high half");
            T.Eq(pay.GetValueOrDefault(1), 50L, "seat 1 wins the low half");
        });

        T.Run("Seven-Card Stud: best seven-card high wins (no board)", () =>
        {
            var holes = new IReadOnlyList<Card>[] { H("9h", "9d", "9s", "9c", "Ah", "Kd", "Qs"), H("Ah", "Ad", "Ks", "Kc", "2h", "3d", "4s") };
            var pay = Showdown.Settle(PokerGames.Of(PokerGame.SevenCardStud), holes, None, 100);
            T.Eq(pay.GetValueOrDefault(0), 100L, "quad nines beat two pair");
        });

        T.Run("Razz: the lowest seven-card hand wins", () =>
        {
            var holes = new IReadOnlyList<Card>[] { H("Ah", "2d", "3s", "4c", "5h", "Kd", "Qc"), H("2h", "3d", "4s", "5c", "7h", "Kh", "Qd") };
            var pay = Showdown.Settle(PokerGames.Of(PokerGame.Razz), holes, None, 100);
            T.Eq(pay.GetValueOrDefault(0), 100L, "the wheel (5-low) beats a 7-low");
        });

        T.Run("Five-Card Draw: best five-card hand wins", () =>
        {
            var holes = new IReadOnlyList<Card>[] { H("Ah", "Kh", "Qh", "Jh", "9h"), H("As", "Ad", "Kc", "Qd", "2h") };
            var pay = Showdown.Settle(PokerGames.Of(PokerGame.FiveCardDraw), holes, None, 100);
            T.Eq(pay.GetValueOrDefault(0), 100L, "a flush beats a pair");
        });
    }
}
