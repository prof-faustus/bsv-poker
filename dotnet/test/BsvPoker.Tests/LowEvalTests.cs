using BsvPoker.Core;
using BsvPoker.Core.Games;

namespace BsvPoker.Tests;

/// <summary>Ace-to-five low evaluation: wheel is best, no-pair beats pairs, 8-or-better qualification, best-of-7.</summary>
public static class LowEvalTests
{
    private static Card C(string s)
    {
        int rank = s[0] switch { 'A' => 14, 'K' => 13, 'Q' => 12, 'J' => 11, 'T' => 10, _ => s[0] - '0' };
        var suit = s[1] switch { 's' => Suit.Spades, 'h' => Suit.Hearts, 'd' => Suit.Diamonds, _ => Suit.Clubs };
        return new Card(rank, suit);
    }
    private static Card[] H(params string[] cs) => cs.Select(C).ToArray();
    private static long L(params string[] cs) => LowEval.Best(H(cs))!.Value.Score;

    public static void All()
    {
        Console.WriteLine("ace-to-five low evaluator:");

        T.Run("the wheel A-2-3-4-5 is the best possible low", () =>
        {
            T.True(L("Ah", "2d", "3s", "4c", "5h") < L("Ah", "2d", "3s", "4c", "6h"), "5-low beats 6-low");
            T.True(L("Ah", "2d", "3s", "4c", "6h") < L("2h", "3d", "4s", "5c", "7h"), "lower top card wins");
            // straights/flushes don't matter: A-2-3-4-5 of one suit is still the nut low
            T.True(L("Ah", "2h", "3h", "4h", "5h") <= L("Ah", "2d", "3s", "4c", "5h"), "flush doesn't spoil the low");
        });

        T.Run("any no-pair low beats any pair (pairs are bad for low)", () =>
        {
            // K-Q-J-T-9 no-pair (a terrible high hand) is still a better LOW than a pair of 2s
            T.True(L("Kh", "Qd", "Js", "Tc", "9h") < L("2h", "2d", "3s", "4c", "5h"), "no-pair < pair for low");
        });

        T.Run("8-or-better qualification", () =>
        {
            T.True(LowEval.Best(H("8h", "5d", "4s", "3c", "2h"), eightOrBetter: true) != null, "8-5-4-3-2 qualifies");
            T.True(LowEval.Best(H("9h", "5d", "4s", "3c", "2h"), eightOrBetter: true) == null, "9-high does not qualify");
            T.True(LowEval.Best(H("8h", "8d", "3s", "2c", "Ah"), eightOrBetter: true) == null, "a pair does not qualify");
            T.True(LowEval.Best(H("8h", "5d", "4s", "3c", "2h")).Value.QualifiesEightOrBetter, "flag set");
        });

        T.Run("best low of seven cards picks the lowest five", () =>
        {
            // seven cards; best low ignores the high pair of kings and takes 6-4-3-2-A
            T.Eq(L("Kh", "Kd", "6s", "4c", "3h", "2d", "Ah"),
                 L("6s", "4c", "3h", "2d", "Ah"), "best-of-7 low equals the explicit 6-low");
        });
    }
}
